using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace NetAndroidProfiler.Core.Weaving;

/// <summary>One woven method in the id map.</summary>
public sealed record WovenMethod(int Id, string Module, int Token, string FullName);

/// <summary>Result of weaving one assembly.</summary>
public sealed record WeaveResult(string InputPath, string OutputPath, IReadOnlyList<WovenMethod> Methods);

/// <summary>
/// Mono.Cecil IL weaver (instrumenting plan B, runtime-independent): wraps the
/// body of every selected method in
/// <c>Profiler.Enter(id); try { ... } finally { Profiler.Leave(id); }</c>,
/// calling the zero-dependency NetAndroidProfiler.Collector assembly, which
/// must be deployed next to the woven assembly. Methods ids are sequential
/// across one <see cref="CecilWeaver"/> instance; the id map is the sidecar
/// the analyzer uses to resolve names.
///
/// Skipped: methods without a body, abstract/extern, compiler-generated types
/// (async/iterator state machines - async attribution is U8), and .cctor
/// (runs during type init, too early for reliable instrumentation).
/// </summary>
public sealed class CecilWeaver
{
    public const string CollectorAssemblyName = "NetAndroidProfiler.Collector";
    private const string CollectorTypeNamespace = "NetAndroidProfiler.Collector";
    private const string CollectorTypeName = "Profiler";

    private readonly WeaveFilter _filter;
    private readonly List<WovenMethod> _map = new();
    private int _nextId;

    public CecilWeaver(WeaveFilter filter, int firstMethodId = 1)
    {
        _filter = filter;
        _nextId = firstMethodId;
    }

    /// <summary>Methods woven so far across all assemblies.</summary>
    public IReadOnlyList<WovenMethod> Map => _map;

    /// <summary>Weave <paramref name="assemblyPath"/> into <paramref name="outputPath"/> (must differ). Returns the per-assembly result; zero methods = nothing matched.</summary>
    public WeaveResult Weave(string assemblyPath, string outputPath)
    {
        if (Path.GetFullPath(assemblyPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must differ from the input path");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
        var module = assembly.MainModule;
        string moduleName = Path.GetFileNameWithoutExtension(assemblyPath);

        var enterRef = ImportCollectorMethod(module, "Enter");
        var leaveRef = ImportCollectorMethod(module, "Leave");

        var woven = new List<WovenMethod>();
        foreach (var type in module.GetTypes())
        {
            if (type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
                continue;
            string ns = type.Namespace ?? "";
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.IsAbstract || method.IsPInvokeImpl) continue;
                if (method.Name == ".cctor") continue;
                if (!_filter.Matches(ns, type.FullName, method.Name)) continue;
                int id = _nextId++;
                WeaveMethod(method, id, enterRef, leaveRef);
                var entry = new WovenMethod(id, moduleName, method.MetadataToken.ToInt32(), $"{type.FullName}.{method.Name}");
                woven.Add(entry);
                _map.Add(entry);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        assembly.Write(outputPath, new WriterParameters { WriteSymbols = false });
        return new WeaveResult(assemblyPath, outputPath, woven);
    }

    /// <summary>Write the id map as a tab-separated sidecar (id, module, token, fullName).</summary>
    public void WriteMap(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new StreamWriter(path);
        w.WriteLine("#napw-map\t1");
        foreach (var m in _map)
            w.WriteLine($"{m.Id}\t{m.Module}\t0x{m.Token:X8}\t{m.FullName}");
    }

    /// <summary>Read a sidecar written by <see cref="WriteMap"/>.</summary>
    public static IReadOnlyList<WovenMethod> ReadMap(string path)
    {
        var list = new List<WovenMethod>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var parts = line.Split('\t');
            list.Add(new WovenMethod(int.Parse(parts[0]), parts[1], Convert.ToInt32(parts[2], 16), parts[3]));
        }
        return list;
    }

    private static MethodReference ImportCollectorMethod(ModuleDefinition module, string name)
    {
        var collectorRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == CollectorAssemblyName);
        if (collectorRef is null)
        {
            collectorRef = new AssemblyNameReference(CollectorAssemblyName, new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(collectorRef);
        }
        var declaring = new TypeReference(CollectorTypeNamespace, CollectorTypeName, module, collectorRef);
        var method = new MethodReference(name, module.TypeSystem.Void, declaring) { HasThis = false };
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        return method;
    }

    /// <summary>Wrap the body in Enter/try/finally/Leave, exception-safe.</summary>
    private static void WeaveMethod(MethodDefinition method, int id, MethodReference enterRef, MethodReference leaveRef)
    {
        var body = method.Body;
        body.SimplifyMacros();
        var il = body.GetILProcessor();

        // Prologue: Profiler.Enter(id) before everything.
        var oldFirst = body.Instructions[0];
        il.InsertBefore(oldFirst, il.Create(OpCodes.Ldc_I4, id));
        il.InsertBefore(oldFirst, il.Create(OpCodes.Call, enterRef));

        // Epilogue skeleton:
        //   <body with every ret replaced by leave -> loadRet>
        //   finallyStart: ldc.i4 id; call Leave; endfinally
        //   loadRet: (ldloc retVal)? ret
        bool hasRet = method.ReturnType.MetadataType != MetadataType.Void;
        VariableDefinition? retVal = null;
        if (hasRet)
        {
            retVal = new VariableDefinition(method.ReturnType);
            body.Variables.Add(retVal);
            body.InitLocals = true;
        }

        Instruction loadRet = hasRet ? il.Create(OpCodes.Ldloc, retVal) : il.Create(OpCodes.Nop);
        Instruction finalRet = il.Create(OpCodes.Ret);
        Instruction finallyStart = il.Create(OpCodes.Ldc_I4, id);
        Instruction callLeave = il.Create(OpCodes.Call, leaveRef);
        Instruction endFinally = il.Create(OpCodes.Endfinally);

        // Replace every ret inside the (future) try block with stloc + leave.
        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        foreach (var ret in rets)
        {
            if (hasRet)
            {
                // ret pops the value: store it first, then leave.
                var stloc = il.Create(OpCodes.Stloc, retVal);
                il.InsertBefore(ret, stloc);
                var leave = il.Create(OpCodes.Leave, loadRet);
                il.Replace(ret, leave);
            }
            else
            {
                il.Replace(ret, il.Create(OpCodes.Leave, loadRet));
            }
        }

        // Append: finally handler + tail.
        var last = body.Instructions[^1];
        il.InsertAfter(last, finallyStart);
        il.InsertAfter(finallyStart, callLeave);
        il.InsertAfter(callLeave, endFinally);
        il.InsertAfter(endFinally, loadRet);
        il.InsertAfter(loadRet, finalRet);

        var tryStart = oldFirst; // Enter stays outside the try: a failed Enter must not fire Leave.
        var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = tryStart,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = loadRet,
        };
        // Existing handlers must stay inside the new try block: ours goes last.
        body.ExceptionHandlers.Add(handler);

        body.OptimizeMacros();
    }
}
