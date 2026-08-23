using NetAndroidDebugger.Core;

namespace NetAndroidDebugger.Tests;

/// <summary>
/// Where a rule gets decided. Reading anything out of the debuggee is forbidden on Mono's event
/// thread, so this predicate is what sends a decision to a worker instead — and getting it wrong
/// either wedges the event thread or silently matches rules against data that is not there.
/// </summary>
public sealed class ExceptionRuleDecisionTests
{
    private const string KnownType = "System.InvalidOperationException";

    [Fact]
    public void ACatchAllRule_DecidesOnTheEventThread()
    {
        ExceptionRule[] rules = [new(ExceptionAction.Log)];

        Assert.False(DebugSession.RulesNeedTheDebuggee(rules, KnownType));
        // Even with no type at all: a rule that asks nothing about the exception needs nothing.
        Assert.False(DebugSession.RulesNeedTheDebuggee(rules, ""));
    }

    [Fact]
    public void ARuleOnTheMessage_AlwaysNeedsTheDebuggee()
    {
        ExceptionRule[] contains = [new(ExceptionAction.Ignore, MessageContains: "pending")];
        ExceptionRule[] regex = [new(ExceptionAction.Ignore, MessageRegex: "pend.ng")];

        Assert.True(DebugSession.RulesNeedTheDebuggee(contains, KnownType));
        Assert.True(DebugSession.RulesNeedTheDebuggee(regex, KnownType));
    }

    /// <summary>
    /// The fix. A type criterion is free when the stop carried a type, and needs a call into the
    /// debuggee when it did not — which is the case for every exception thrown in an assembly
    /// without debug info, and those are precisely the third-party ones a rule wants to silence.
    /// Matching "Mqtt" against an empty string would quietly ignore the rule.
    /// </summary>
    [Fact]
    public void ARuleOnTheType_NeedsTheDebuggee_OnlyWhenTheStopCarriedNoType()
    {
        ExceptionRule[] exact = [new(ExceptionAction.Ignore, Type: "MQTTnet.Exceptions.MqttCommunicationTimedOutException")];
        ExceptionRule[] partial = [new(ExceptionAction.Ignore, TypeContains: "Mqtt")];

        Assert.False(DebugSession.RulesNeedTheDebuggee(exact, KnownType));
        Assert.False(DebugSession.RulesNeedTheDebuggee(partial, KnownType));

        Assert.True(DebugSession.RulesNeedTheDebuggee(exact, ""));
        Assert.True(DebugSession.RulesNeedTheDebuggee(partial, null));
    }

    /// <summary>
    /// The raise site comes from the backtrace the stop already carries, so it never costs a call
    /// — worth keeping straight, because it is the cheap criterion to reach for on a noisy app.
    /// </summary>
    [Fact]
    public void ARuleOnTheSourceFile_NeverNeedsTheDebuggee()
    {
        ExceptionRule[] rules = [new(ExceptionAction.Break, SourceFileContains: "MqttService")];

        Assert.False(DebugSession.RulesNeedTheDebuggee(rules, KnownType));
        Assert.False(DebugSession.RulesNeedTheDebuggee(rules, ""));
    }

    /// <summary>One rule needing the debuggee is enough, wherever it sits in the list.</summary>
    [Fact]
    public void OneRuleThatNeedsIt_IsEnough()
    {
        ExceptionRule[] rules =
        [
            new(ExceptionAction.Ignore, TypeContains: "Certificate"),
            new(ExceptionAction.Log, MessageContains: "connect/disconnect is pending"),
            new(ExceptionAction.Break),
        ];

        Assert.True(DebugSession.RulesNeedTheDebuggee(rules, KnownType));
    }

    [Fact]
    public void NoRulesAtAll_NeedNothing()
    {
        Assert.False(DebugSession.RulesNeedTheDebuggee([], ""));
    }
}
