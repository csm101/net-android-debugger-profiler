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

    /// <summary>Methods that matched the filter but could not be safely woven (skipped, left original).</summary>
    public int SkippedCount { get; private set; }

    /// <summary>Weave <paramref name="assemblyPath"/> into <paramref name="outputPath"/> (must differ). Returns the per-assembly result; zero methods = nothing matched. Pass a custom <paramref name="resolver"/> to satisfy references that are not next to the input (e.g. pulled from a device on demand).</summary>
    public WeaveResult Weave(string assemblyPath, string outputPath, IAssemblyResolver? resolver = null)
    {
        if (Path.GetFullPath(assemblyPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must differ from the input path");

        if (resolver is DefaultAssemblyResolver dar)
            dar.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
        else if (resolver is null)
        {
            var d = new DefaultAssemblyResolver();
            d.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
            resolver = d;
        }
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
                if (!CanWeave(method)) continue;
                if (!_filter.Matches(ns, type.FullName, method.Name)) continue;
                int id = _nextId;
                var snapshot = BodySnapshot.Capture(method.Body);
                try
                {
                    WeaveMethod(method, id, enterRef, leaveRef);
                    // Validate the rewritten body: this is where malformed control flow surfaces.
                    method.Body.OptimizeMacros();
                }
                catch (Exception)
                {
                    // A method shape the weaver cannot handle (unusual control flow, protected
                    // regions, switches): restore it untouched and skip. Never abort the whole run.
                    snapshot.Restore(method.Body);
                    SkippedCount++;
                    continue;
                }
                _nextId++;
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

    /// <summary>Methods the weaver cannot safely wrap and skips.</summary>
    internal static bool CanWeave(MethodDefinition method)
    {
        if (!method.HasBody || method.IsAbstract || method.IsPInvokeImpl) return false;
        if (method.Name is ".cctor") return false;
        if (method.Body.Instructions.Count == 0) return false;
        // ref-returning methods: the return value cannot be stashed in a local for the finally tail.
        if (method.ReturnType.IsByReference) return false;
        return true;
    }

    /// <summary>
    /// Wrap the body in <c>Enter(id); try { ... } finally { Leave(id); } return</c>,
    /// exception-safe. Every original <c>ret</c> is turned into a branch out of
    /// the try by mutating the instruction in place (so existing branch targets
    /// that pointed at it stay valid), storing the return value in a local first.
    /// </summary>
    private static void WeaveMethod(MethodDefinition method, int id, MethodReference enterRef, MethodReference leaveRef)
    {
        var body = method.Body;
        body.SimplifyMacros();
        var il = body.GetILProcessor();

        bool hasRet = method.ReturnType.MetadataType != MetadataType.Void;
        VariableDefinition? retVal = null;
        if (hasRet)
        {
            retVal = new VariableDefinition(method.ReturnType);
            body.Variables.Add(retVal);
            body.InitLocals = true;
        }

        var oldFirst = body.Instructions[0];

        // Tail after the finally: (ldloc retVal)? ret
        Instruction loadRet = hasRet ? il.Create(OpCodes.Ldloc, retVal) : Instruction.Create(OpCodes.Nop);
        Instruction finalRet = il.Create(OpCodes.Ret);
        Instruction finallyStart = il.Create(OpCodes.Ldc_I4, id);
        Instruction callLeave = il.Create(OpCodes.Call, leaveRef);
        Instruction endFinally = il.Create(OpCodes.Endfinally);

        // Turn every original ret into "store (if value) + leave loadRet", mutating in place
        // so branches targeting the ret still hit a valid instruction.
        foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            if (hasRet)
            {
                // ins currently: ret (value on stack). Become: stloc retVal ; leave loadRet.
                ins.OpCode = OpCodes.Stloc;
                ins.Operand = retVal;
                il.InsertAfter(ins, il.Create(OpCodes.Leave, loadRet));
            }
            else
            {
                ins.OpCode = OpCodes.Leave;
                ins.Operand = loadRet;
            }
        }

        // Append the finally handler and the tail.
        var last = body.Instructions[^1];
        il.InsertAfter(last, finallyStart);
        il.InsertAfter(finallyStart, callLeave);
        il.InsertAfter(callLeave, endFinally);
        il.InsertAfter(endFinally, loadRet);
        il.InsertAfter(loadRet, finalRet);

        // Prologue: Profiler.Enter(id) before the (now protected) body. Enter stays
        // outside the try so a failed Enter cannot trigger Leave.
        il.InsertBefore(oldFirst, il.Create(OpCodes.Ldc_I4, id));
        il.InsertBefore(oldFirst, il.Create(OpCodes.Call, enterRef));

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = oldFirst,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = loadRet,
        });
    }

    /// <summary>Captured state of a method body, to roll back a failed weave.</summary>
    private sealed class BodySnapshot
    {
        private readonly List<(Instruction ins, OpCode op, object? operand)> _instructions;
        private readonly List<VariableDefinition> _variables;
        private readonly List<ExceptionHandler> _handlers;
        private readonly bool _initLocals;

        private BodySnapshot(List<(Instruction, OpCode, object?)> ins, List<VariableDefinition> vars, List<ExceptionHandler> handlers, bool initLocals)
        {
            _instructions = ins; _variables = vars; _handlers = handlers; _initLocals = initLocals;
        }

        public static BodySnapshot Capture(MethodBody body)
        {
            var ins = body.Instructions.Select(i => (i, i.OpCode, i.Operand)).ToList();
            return new BodySnapshot(ins, body.Variables.ToList(), body.ExceptionHandlers.ToList(), body.InitLocals);
        }

        public void Restore(MethodBody body)
        {
            body.Instructions.Clear();
            foreach (var (ins, op, operand) in _instructions) { ins.OpCode = op; ins.Operand = operand; body.Instructions.Add(ins); }
            body.Variables.Clear();
            foreach (var v in _variables) body.Variables.Add(v);
            body.ExceptionHandlers.Clear();
            foreach (var h in _handlers) body.ExceptionHandlers.Add(h);
            body.InitLocals = _initLocals;
        }
    }
}
