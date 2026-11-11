using System.ComponentModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-10 R15: <c>power</c> when the OS says no. <c>SetSuspendState</c> / <c>LockWorkStation</c> /
/// <c>ExitWindowsEx</c> returning false was thrown as <see cref="Win32Exception"/> ("Sleep failed
/// (suspend may be disabled)"), which the SDK masks — but hibernate with <c>powercfg /h off</c> or
/// lock with no interactive desktop is the OS REFUSING the request, and the caller has to read
/// that. The token-privilege plumbing failing is a genuine fault and stays a
/// <see cref="Win32Exception"/>. Driven through <see cref="IPowerNative"/> with a recording fake:
/// no test here ever invokes a real power action.
/// </summary>
[Trait("Category", "Unit")]
public class PowerServiceTests
{
    private sealed class FakePowerNative : IPowerNative
    {
        public List<string> Calls { get; } = [];
        public bool Succeed { get; init; } = true;
        public int Error { get; init; }
        public Exception? PrivilegeFault { get; init; }

        public void EnableShutdownPrivilege()
        {
            Calls.Add("privilege");
            if (PrivilegeFault is not null) throw PrivilegeFault;
        }

        public bool ExitWindows(PowerExitKind kind, out int error) => Record($"exit:{kind}", out error);
        public bool LockWorkStation(out int error) => Record("lock", out error);
        public bool SetSuspendState(bool hibernate, out int error) => Record(hibernate ? "suspend:hibernate" : "suspend:sleep", out error);

        private bool Record(string call, out int error)
        {
            Calls.Add(call);
            error = Succeed ? 0 : Error;
            return Succeed;
        }
    }

    // ---- the dispatch: which native call each action makes -----------------------------------

    [Theory]
    [InlineData("shutdown", new[] { "privilege", "exit:Shutdown" })]
    [InlineData("reboot", new[] { "privilege", "exit:Reboot" })]
    [InlineData("logoff", new[] { "exit:Logoff" })]   // logoff needs no privilege
    [InlineData("lock", new[] { "lock" })]
    [InlineData("sleep", new[] { "suspend:sleep" })]
    [InlineData("hibernate", new[] { "suspend:hibernate" })]
    [InlineData("SLEEP", new[] { "suspend:sleep" })]   // case-insensitive, as before
    public async Task Each_action_makes_exactly_its_native_call_in_order(string action, string[] expected)
    {
        var native = new FakePowerNative();

        await new PowerService(native).ExecuteAsync(action);

        native.Calls.Should().Equal(expected, "the privilege is enabled BEFORE ExitWindowsEx, and nothing else is called");
    }

    // ---- the OS refuses: a caller-facing refusal carrying the action and the reason ----------

    [Theory]
    [InlineData("shutdown", 1190)]   // ERROR_SHUTDOWN_IS_SCHEDULED
    [InlineData("reboot", 1115)]     // ERROR_SHUTDOWN_IN_PROGRESS
    [InlineData("logoff", 5)]
    [InlineData("lock", 5)]          // no interactive desktop
    [InlineData("sleep", 50)]        // ERROR_NOT_SUPPORTED: suspend disabled
    [InlineData("hibernate", 50)]    // powercfg /h off
    public async Task An_OS_refusal_is_an_InvalidOperationException_naming_the_action_and_the_OS_reason(string action, int error)
    {
        var native = new FakePowerNative { Succeed = false, Error = error };

        Func<Task> act = () => new PowerService(native).ExecuteAsync(action);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "a Win32Exception here reached the caller as \"An error occurred invoking 'power'.\"")).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain(action);
        ex.Message.Should().Contain(new Win32Exception(error).Message, "the OS says why; the caller cannot fix what it is not told");
    }

    [Fact]
    public async Task An_OS_refusal_with_no_error_code_is_still_a_refusal_naming_the_action()
    {
        // SetSuspendState can fail without setting a last error; that must not read as success
        // nor as an empty reason.
        var native = new FakePowerNative { Succeed = false, Error = 0 };

        Func<Task> act = () => new PowerService(native).ExecuteAsync("hibernate");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("hibernate");
    }

    /// <summary>
    /// GREEN round 2: an error code Windows has no message for. <c>Marshal.GetPInvokeErrorMessage</c>
    /// does not return "" for one — .NET falls back to "Unknown error (0x…)" when FormatMessage
    /// fails — but the refusal must not depend on that: whatever the lookup says, the caller still
    /// reads the action and the numeric code, and the reason slot is never blank.
    /// </summary>
    [Theory]
    [InlineData("sleep", 987654)]          // no system message for this code
    [InlineData("hibernate", 0x7FFFFFFF)]
    public async Task An_OS_refusal_with_an_unmapped_error_code_still_names_the_action_and_the_code(string action, int error)
    {
        var native = new FakePowerNative { Succeed = false, Error = error };

        Func<Task> act = () => new PowerService(native).ExecuteAsync(action);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain($"'{action}'");
        message.Should().Contain($"(Win32 error {error})", "the code is the one thing the caller can look up");
        message.Should().MatchRegex(@"': \S.* \(Win32 error ", "the reason before the code is never blank");
        message.Should().NotContain(":  (", "an empty OS message must not leave a hole in the sentence");
    }

    /// <summary>
    /// The public constructor is the one the DI container uses (WindowsMcpHost registers
    /// <c>AddSingleton&lt;IPowerService, PowerService&gt;</c>, and MS DI only sees public
    /// constructors). Driven with an unknown action so no native call can happen.
    /// </summary>
    [Fact]
    public async Task The_container_built_service_refuses_an_unknown_action_without_any_native_call()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IPowerService, PowerService>()
            .BuildServiceProvider();
        var power = provider.GetRequiredService<IPowerService>();

        Func<Task> act = () => power.ExecuteAsync("suspend");

        power.Should().BeOfType<PowerService>();
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("'suspend'");
    }

    // ---- the token plumbing: a fault, not a refusal -------------------------------------------

    [Theory]
    [InlineData("shutdown")]
    [InlineData("reboot")]
    public async Task A_privilege_plumbing_fault_stays_a_Win32Exception_and_nothing_is_attempted(string action)
    {
        var fault = new Win32Exception(6, "OpenProcessToken failed");   // ERROR_INVALID_HANDLE
        var native = new FakePowerNative { PrivilegeFault = fault };

        Func<Task> act = () => new PowerService(native).ExecuteAsync(action);

        (await act.Should().ThrowAsync<Win32Exception>()).Which.Should().BeSameAs(fault,
            "token plumbing failing is our fault, masked on purpose (ToolErrorsTests.MaskedOnPurpose)");
        native.Calls.Should().Equal(["privilege"], "ExitWindowsEx is never called without the privilege");
    }

    [Fact]
    public async Task The_privilege_not_held_refusal_passes_through_unchanged()
    {
        // ERROR_NOT_ALL_ASSIGNED is already an InvalidOperationException naming SeShutdownPrivilege.
        var refusal = new InvalidOperationException("SeShutdownPrivilege is not held by this process token; cannot shut down or reboot.");
        var native = new FakePowerNative { PrivilegeFault = refusal };

        Func<Task> act = () => new PowerService(native).ExecuteAsync("shutdown");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(refusal);
    }

    // ---- unknown action: refused before anything native ---------------------------------------

    [Theory]
    [InlineData("suspend")]
    [InlineData("")]
    public async Task An_unknown_action_is_an_ArgumentException_before_any_native_call(string action)
    {
        var native = new FakePowerNative();

        Func<Task> act = () => new PowerService(native).ExecuteAsync(action);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("shutdown|reboot|logoff|lock|sleep|hibernate");
        native.Calls.Should().BeEmpty();
    }
}

/// <summary>
/// D-10 R15 through the REAL token plumbing — the only part of <see cref="Win32PowerNative"/> a
/// test may call (every other member performs a power action). Enabling SeShutdownPrivilege in the
/// test host's own token changes nothing outside this process and dies with it. The contract: it
/// either succeeds, or refuses with the caller-facing InvalidOperationException naming the
/// privilege (a token that does not hold it); a Win32Exception would mean the plumbing itself is
/// broken, which is the masked-on-purpose case and must not happen on a healthy box.
/// </summary>
[Trait("Category", "Integration")]
public class Win32PowerNativeIntegrationTests
{
    [Fact]
    public void EnableShutdownPrivilege_succeeds_or_refuses_naming_the_privilege()
    {
        var act = () => new Win32PowerNative().EnableShutdownPrivilege();

        try
        {
            act();
        }
        catch (InvalidOperationException ex)
        {
            ex.Message.Should().Contain("SeShutdownPrivilege");
            ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        }
    }
}
