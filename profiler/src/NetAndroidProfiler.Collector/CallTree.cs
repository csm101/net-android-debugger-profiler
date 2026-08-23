using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NetAndroidProfiler.Collector;

/// <summary>
/// Per-thread calling context tree: one node per call path, not per call.
///
/// This is the difference between "instrument a namespace" and "instrument the app". The
/// event stream (see <see cref="Profiler"/>'s trace mode) writes a record per enter and
/// per leave, so its cost and its volume grow with the number of calls; a tight leaf loop
/// drowns in it. Here a call updates counters on the node for its path - a timestamp and a
/// few adds - and the data that leaves the process grows with the number of distinct call
/// paths, which is bounded by the program's shape.
///
/// What is kept is what a call tree, a call graph, parents/children and a critical path are
/// built from: calls, inclusive and exclusive time, minimum and maximum, per path and per
/// thread. What is lost is the order in which calls happened and the duration of any single
/// one beyond min and max - the price of not streaming.
/// </summary>
internal sealed class CallTree
{
    /// <summary>Deeper than this and recursion is folded onto the deepest node (AQTime does the same).</summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Nodes live in arrays published as one object, so a reader that takes a snapshot while
    /// the owning thread is running sees a complete set of arrays and a count that is never
    /// ahead of the nodes it can read.
    /// </summary>
    internal sealed class Nodes
    {
        public int[] Parent;
        public int[] Method;
        /// <summary>Children as a linked list in arrays: no hashing on the instrumented path.</summary>
        public int[] FirstChild;
        public int[] NextSibling;
        public long[] Calls;
        public long[] Inclusive;
        public long[] Exclusive;
        public long[] Min;
        public long[] Max;
        public int Count;

        public Nodes(int capacity)
        {
            Parent = new int[capacity];
            Method = new int[capacity];
            FirstChild = new int[capacity];
            NextSibling = new int[capacity];
            Calls = new long[capacity];
            Inclusive = new long[capacity];
            Exclusive = new long[capacity];
            Min = new long[capacity];
            Max = new long[capacity];
        }

        public Nodes Grow()
        {
            var bigger = new Nodes(Parent.Length * 2);
            Array.Copy(Parent, bigger.Parent, Count);
            Array.Copy(Method, bigger.Method, Count);
            Array.Copy(FirstChild, bigger.FirstChild, Count);
            Array.Copy(NextSibling, bigger.NextSibling, Count);
            Array.Copy(Calls, bigger.Calls, Count);
            Array.Copy(Inclusive, bigger.Inclusive, Count);
            Array.Copy(Exclusive, bigger.Exclusive, Count);
            Array.Copy(Min, bigger.Min, Count);
            Array.Copy(Max, bigger.Max, Count);
            bigger.Count = Count;
            return bigger;
        }
    }

    private Nodes _nodes = new Nodes(256);
    /// <summary>Head of the root nodes' list; -1 until the first call.</summary>
    private int _firstRoot = -1;

    private int[] _stackNode = new int[MaxDepth + 1];
    private long[] _stackEnter = new long[MaxDepth + 1];
    private long[] _stackChild = new long[MaxDepth + 1];
    private int _depth;

    public readonly int ThreadId;
    public int Generation;

    public CallTree(int threadId, int generation)
    {
        ThreadId = threadId;
        Generation = generation;
    }

    /// <summary>The node set as it stands; safe to read from another thread.</summary>
    public Nodes Snapshot() => Volatile.Read(ref _nodes);

    public void Enter(int methodId, long now)
    {
        if (_depth >= MaxDepth)
        {
            // Folded: the call still costs time, and that time belongs to the node we are
            // already in. Losing the shape below a 64-deep recursion is the cheaper mistake.
            _depth++;
            return;
        }

        int parent = _depth > 0 ? _stackNode[_depth - 1] : -1;
        int node = Child(parent, methodId);
        _stackNode[_depth] = node;
        _stackEnter[_depth] = now;
        _stackChild[_depth] = 0;
        _depth++;
    }

    public void Leave(int methodId, long now)
    {
        if (_depth <= 0) return;                       // a leave without its enter: ignore
        _depth--;
        if (_depth >= MaxDepth) return;                // was folded on the way in

        int node = _stackNode[_depth];
        var nodes = _nodes;
        // A leave for a method other than the one on top means frames were skipped (an
        // exception unwound through code we did not instrument): unwind to it, or give up
        // rather than attribute time to the wrong path.
        if (nodes.Method[node] != methodId)
        {
            int probe = _depth;
            while (probe > 0 && nodes.Method[_stackNode[probe]] != methodId) probe--;
            if (nodes.Method[_stackNode[probe]] != methodId) return;
            _depth = probe;
            node = _stackNode[_depth];
        }

        long elapsed = now - _stackEnter[_depth];
        if (elapsed < 0) elapsed = 0;
        long exclusive = elapsed - _stackChild[_depth];
        if (exclusive < 0) exclusive = 0;

        nodes.Calls[node]++;
        nodes.Inclusive[node] += elapsed;
        nodes.Exclusive[node] += exclusive;
        if (nodes.Min[node] == 0 || elapsed < nodes.Min[node]) nodes.Min[node] = elapsed;
        if (elapsed > nodes.Max[node]) nodes.Max[node] = elapsed;

        if (_depth > 0) _stackChild[_depth - 1] += elapsed;
    }

    private int Child(int parent, int methodId)
    {
        var nodes = _nodes;
        // Walk this node's children. A method calls few distinct methods, so the list is
        // short and a walk of it beats hashing a key on every call.
        for (int i = parent >= 0 ? nodes.FirstChild[parent] : _firstRoot; i >= 0; i = nodes.NextSibling[i])
            if (nodes.Method[i] == methodId)
                return i;

        if (nodes.Count == nodes.Parent.Length)
        {
            nodes = nodes.Grow();
            Volatile.Write(ref _nodes, nodes);
        }
        int index = nodes.Count;
        nodes.Parent[index] = parent;
        nodes.Method[index] = methodId;
        nodes.FirstChild[index] = -1;
        nodes.NextSibling[index] = parent >= 0 ? nodes.FirstChild[parent] : _firstRoot;
        // Publish the count only once the node is complete: a reader never sees a half-built one.
        Volatile.Write(ref nodes.Count, index + 1);
        if (parent >= 0) nodes.FirstChild[parent] = index; else _firstRoot = index;
        return index;
    }
}
