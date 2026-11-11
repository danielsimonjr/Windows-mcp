using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.Win32;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-10 R14: registry access-denied, one caller-facing shape on every path. <c>OpenSubKey</c>
/// throws <see cref="SecurityException"/> ("Requested registry access is not allowed") — a
/// SystemException the call-tool filter masks — while <c>CreateSubKey</c> / <c>DeleteSubKey</c>
/// throw an <see cref="UnauthorizedAccessException"/> that may not name the key at all. The
/// requirement: every registry path reports access-denied as an
/// <see cref="UnauthorizedAccessException"/> naming the hive and the key path.
/// </summary>
[Trait("Category", "Unit")]
public class RegistryErrorsTests
{
    private const string Hive = "HKLM";
    private const string KeyPath = "WindowsMcpTestsDenied";

    public static TheoryData<Exception> AccessDenied => new()
    {
        new SecurityException("Requested registry access is not allowed."),
        new UnauthorizedAccessException(),   // DeleteSubKey's bare form: "Attempted to perform an unauthorized operation."
        new UnauthorizedAccessException("Access to the registry key 'HKEY_LOCAL_MACHINE\\WindowsMcpTestsDenied' is denied."),
    };

    [Theory]
    [MemberData(nameof(AccessDenied))]
    public void Access_denied_in_any_form_is_an_UnauthorizedAccessException_naming_the_hive_and_the_path(Exception ex)
    {
        var translated = RegistryErrors.Translate(ex, Hive, KeyPath);

        translated.Should().BeOfType<UnauthorizedAccessException>();
        ToolErrors.IsCallerFacing(translated!).Should().BeTrue();
        translated!.Message.Should().Contain(Hive, "the hive as the caller named it");
        translated.Message.Should().Contain(KeyPath);
        translated.InnerException.Should().BeSameAs(ex);
    }

    public static TheoryData<Exception> NotAccessDenied => new()
    {
        new KeyNotFoundException("Registry path not found"),
        new IOException("value missing"),
        new ArgumentException("Unknown hive", "hive"),
        new InvalidOperationException("has 3 sub-key(s); pass recursive:true"),
        new NullReferenceException(),
    };

    [Theory]
    [MemberData(nameof(NotAccessDenied))]
    public void Anything_else_is_left_alone(Exception ex)
        => RegistryErrors.Translate(ex, Hive, KeyPath).Should().BeNull();

    // ---- GREEN round 2: Guard, the wrapper every RegistryService operation runs in -------------

    [Fact]
    public void Guard_returns_what_the_operation_returned()
        => RegistryErrors.Guard(Hive, KeyPath, () => 42).Should().Be(42);

    /// <summary>
    /// The refusals RegistryService throws from INSIDE Guard — the not-found
    /// KeyNotFoundException, the "pass recursive:true" InvalidOperationException, an IOException
    /// for a missing value — must reach the caller as the very same exception: not wrapped, not
    /// re-typed, not re-worded as access-denied.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotAccessDenied))]
    public void Guard_lets_every_other_exception_through_as_the_same_instance(Exception ex)
    {
        var act = () => RegistryErrors.Guard<int>(Hive, KeyPath, () => throw ex);

        act.Should().Throw<Exception>().Which.Should().BeSameAs(ex);
    }

    [Fact]
    public void Guard_lets_a_cancellation_through_as_the_same_instance()
    {
        var cancelled = new OperationCanceledException();

        var act = () => RegistryErrors.Guard<int>(Hive, KeyPath, () => throw cancelled);

        act.Should().Throw<OperationCanceledException>().Which.Should().BeSameAs(cancelled);
    }

    [Theory]
    [MemberData(nameof(AccessDenied))]
    public void Guard_turns_access_denied_into_the_one_shape_naming_the_hive_and_the_key(Exception ex)
    {
        var act = () => RegistryErrors.Guard<int>(Hive, KeyPath, () => throw ex);

        var thrown = act.Should().Throw<UnauthorizedAccessException>().Which;
        thrown.Should().NotBeSameAs(ex, "the bare forms name nothing; the translation names the key");
        thrown.Message.Should().Contain($"{Hive}\\{KeyPath}");
        thrown.InnerException.Should().BeSameAs(ex);
    }

    // ---- D-10 R25 (round 3): what the message claims -------------------------------------------

    private const string PartWay = "(?i)part[- ]?way";
    private const string MayBeRemoved = "(?i)sub-?keys? may already have been (removed|deleted)";

    [Theory]
    [MemberData(nameof(AccessDenied))]
    public void A_denial_part_way_through_a_tree_delete_says_so_and_that_sub_keys_may_be_gone(Exception ex)
    {
        // R25a: DeleteSubKeyTree is not atomic (pre-existing, logged separately). When it is denied
        // after it started, sub-keys are already deleted; a message that reads as a clean refusal
        // tells the caller nothing happened.
        var translated = RegistryErrors.Translate(ex, Hive, KeyPath, partial: true);

        translated.Should().BeOfType<UnauthorizedAccessException>();
        translated!.Message.Should().Contain($"{Hive}\\{KeyPath}");
        translated.Message.Should().MatchRegex(PartWay, "the delete was denied part-way");
        translated.Message.Should().MatchRegex(MayBeRemoved, "and the caller has to know the tree may be half gone");
        translated.InnerException.Should().BeSameAs(ex);
    }

    [Theory]
    [MemberData(nameof(AccessDenied))]
    public void A_clean_refusal_does_not_claim_anything_was_removed(Exception ex)
    {
        var message = RegistryErrors.Translate(ex, Hive, KeyPath)!.Message;

        message.Should().NotMatchRegex(PartWay);
        message.Should().NotMatchRegex(MayBeRemoved);
    }

    [Fact]
    public void A_partial_flag_on_something_that_is_not_access_denied_is_still_left_alone()
        => RegistryErrors.Translate(new IOException("x"), Hive, KeyPath, partial: true).Should().BeNull();

    // ---- GREEN round 3: Guard inside Guard (CountDescendants and the tree delete) ------------------

    private const string Child = KeyPath + "\\locked";

    [Theory]
    [MemberData(nameof(AccessDenied))]
    public void An_outer_Guard_leaves_a_refusal_an_inner_Guard_already_translated_untouched(Exception ex)
    {
        // RegistryService.DeleteKeyAsync runs CountDescendants inside its own Guard(hive, path), and
        // CountDescendants guards each descendant by ITS path. The descendant's refusal is the more
        // precise one; re-translating it in the outer Guard would name the parent instead.
        UnauthorizedAccessException? inner = null;
        var act = () => RegistryErrors.Guard<int>(Hive, KeyPath, () =>
        {
            try { return RegistryErrors.Guard<int>(Hive, Child, () => throw ex); }
            catch (UnauthorizedAccessException caught) { inner = caught; throw; }
        });

        var thrown = act.Should().Throw<UnauthorizedAccessException>().Which;
        thrown.Should().BeSameAs(inner, "the outer Guard rethrows the inner refusal as it is");
        thrown.Message.Should().Contain($"{Hive}\\{Child}");
        thrown.InnerException.Should().BeSameAs(ex);
    }

    [Theory]
    [MemberData(nameof(AccessDenied))]
    public void An_outer_Guard_keeps_an_inner_part_way_refusal_part_way(Exception ex)
    {
        // The tree delete runs under Guard(partial: true) inside the clean outer Guard: the outer one
        // must not turn "denied part-way" back into a clean refusal.
        var act = () => RegistryErrors.Guard<int>(Hive, KeyPath,
            () => RegistryErrors.Guard<int>(Hive, KeyPath, () => throw ex, partial: true));

        act.Should().Throw<UnauthorizedAccessException>().Which.Message
            .Should().MatchRegex(PartWay).And.MatchRegex(MayBeRemoved);
    }

    [Theory]
    [MemberData(nameof(AccessDenied))]
    public void A_fresh_denial_on_the_parent_after_an_inner_Guard_succeeded_is_still_translated(Exception ex)
    {
        // The marker is on the TRANSLATED exception only: a parent that refuses on its own (the
        // DeleteSubKey after a clean count) is still named, as before the marker existed.
        var act = () => RegistryErrors.Guard<int>(Hive, KeyPath, () =>
        {
            _ = RegistryErrors.Guard(Hive, Child, () => 1);
            throw ex;
        });

        var thrown = act.Should().Throw<UnauthorizedAccessException>().Which;
        thrown.Should().NotBeSameAs(ex);
        thrown.Message.Should().Contain($"'{Hive}\\{KeyPath}'");
        thrown.InnerException.Should().BeSameAs(ex);
    }

    [Fact]
    public void A_translated_refusal_leaves_the_original_exceptions_Data_alone()
    {
        // The marker goes on the exception Translate builds, never on the one it was handed: an
        // UnauthorizedAccessException that is retried or re-guarded later must still translate.
        var original = new UnauthorizedAccessException();

        RegistryErrors.Translate(original, Hive, KeyPath);

        original.Data.Count.Should().Be(0);
        RegistryErrors.Translate(original, Hive, KeyPath).Should().NotBeNull("the original can be translated again");
    }

    [Theory]
    [InlineData("HKLM")]
    [InlineData("HKCU")]
    [InlineData("HKEY_USERS")]
    public void An_empty_path_names_the_hive_alone(string hive)
    {
        // R25c: the hive root (list / enumerate with path "") rendered as 'HKLM\'.
        var message = RegistryErrors.Translate(new SecurityException("denied"), hive, "")!.Message;

        message.Should().Contain($"'{hive}'");
        message.Should().NotContain($"{hive}\\", "there is no key below the hive to separate");
    }

    [Theory]
    [InlineData("HKCU")]
    [InlineData("HKEY_CURRENT_USER")]
    [InlineData("HKU")]
    [InlineData("HKCR")]
    public void The_hint_does_not_claim_HKLM_for_a_key_outside_it(string hive)
    {
        // R25d: "Protected keys (most of HKLM) need the server to run elevated" on an HKCU key
        // points the caller at the wrong hive. The wording is neutral, or there is no hint.
        var message = RegistryErrors.Translate(new UnauthorizedAccessException(), hive, @"Software\Locked")!.Message;

        message.Should().NotContain("HKLM").And.NotContain("HKEY_LOCAL_MACHINE");
    }
}

/// <summary>
/// D-10 R14 through the real <see cref="RegistryService"/>. No protected key is written to or
/// deleted from: every test builds its own key under <c>HKCU\Software\WindowsMcp.Tests\&lt;guid&gt;</c>,
/// puts a DENY entry for the current user on it (the owner keeps READ_CONTROL and WRITE_DAC
/// whatever the DACL says, so the deny can always be taken off again), and Dispose purges the
/// deny entries and deletes the tree.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RegistryServiceAccessDeniedIntegrationTests : IDisposable
{
    private const string Hive = "HKCU";
    private readonly string _root = $"Software\\WindowsMcp.Tests\\{Guid.NewGuid():N}";
    private readonly List<string> _denied = [];
    private readonly SecurityIdentifier _me = WindowsIdentity.GetCurrent().User!;
    private readonly RegistryService _svc = new();

    public RegistryServiceAccessDeniedIntegrationTests()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_root, writable: true);
        key.SetValue("keep", "value");
    }

    public void Dispose()
    {
        foreach (var path in _denied)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
                if (key is null) continue;
                var acl = key.GetAccessControl();
                acl.PurgeAccessRules(_me);   // a fresh key has only inherited entries; ours are the explicit ones
                key.SetAccessControl(acl);
            }
            catch { /* best effort */ }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); } catch { }
    }

    private void Deny(string path, RegistryRights rights)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!;
        var acl = key.GetAccessControl();
        acl.AddAccessRule(new RegistryAccessRule(_me, rights, AccessControlType.Deny));
        key.SetAccessControl(acl);
        _denied.Add(path);
    }

    private string Child(string name)
    {
        var path = _root + "\\" + name;
        Registry.CurrentUser.CreateSubKey(path, writable: true).Dispose();
        return path;
    }

    private static async Task<UnauthorizedAccessException> DeniedAsync(Func<Task> act, string path)
    {
        var ex = (await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "access-denied is a refusal the caller reads, never a masked SecurityException")).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain(path, "the message names the key");
        (ex.Message.Contains("HKCU", StringComparison.Ordinal) || ex.Message.Contains("HKEY_CURRENT_USER", StringComparison.Ordinal))
            .Should().BeTrue($"the message names the hive: {ex.Message}");
        return ex;
    }

    // ---- a key the caller may not READ: every read path -------------------------------------

    private const RegistryRights NoRead = RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys;

    public static TheoryData<string> ReadPaths => new()
    {
        "get_names", "get_value", "list", "enumerate_values", "enumerate_subkeys", "delete_key",
    };

    private Task Read(string op, string path) => op switch
    {
        "get_names" => _svc.GetAsync(Hive, path, null),
        "get_value" => _svc.GetAsync(Hive, path, "keep"),
        "list" => _svc.ListAsync(Hive, path),
        "enumerate_values" => _svc.EnumerateValuesAsync(Hive, path),
        "enumerate_subkeys" => _svc.EnumerateSubKeysAsync(Hive, path),
        "delete_key" => _svc.DeleteKeyAsync(Hive, path, recursive: false),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "no such op"),
    };

    [Theory]
    [MemberData(nameof(ReadPaths))]
    public async Task Every_read_of_a_key_the_caller_may_not_read_is_an_UnauthorizedAccessException_naming_it(string op)
    {
        Deny(_root, NoRead);

        await DeniedAsync(() => Read(op, _root), _root);
    }

    [Fact]
    public async Task A_recursive_delete_refused_inside_the_descendant_count_names_the_key_and_deletes_nothing()
    {
        // CountDescendants opens every child for read before anything is deleted; a child the
        // caller may not read must refuse the whole delete, not mask it, and not half-delete.
        var locked = Child("locked");
        Child("open");
        Deny(locked, NoRead);

        var ex = await DeniedAsync(() => _svc.DeleteKeyAsync(Hive, _root, recursive: true), _root);

        // R25b (round 3): the parent is readable; the key that refused is the descendant, so the
        // message names THAT key's full path — naming only the parent sends the caller to the wrong key.
        ex.Message.Should().Contain(locked, "the descendant that denied the read is the one named");
        ex.Message.Should().NotMatchRegex("(?i)part[- ]?way", "nothing was deleted: the count runs before any mutation");
        using var still = Registry.CurrentUser.OpenSubKey(_root);
        still.Should().NotBeNull("the refusal came before any mutation");
        still!.GetSubKeyNames().Should().BeEquivalentTo(["locked", "open"]);
    }

    [Fact]
    public async Task A_recursive_delete_refused_by_a_grandchild_in_the_count_names_the_grandchild()
    {
        Child("mid");
        var deep = Child("mid\\deep");
        Child("open");
        Deny(deep, NoRead);

        var ex = await DeniedAsync(() => _svc.DeleteKeyAsync(Hive, _root, recursive: true), _root);

        ex.Message.Should().Contain(deep, "the full path of the key that refused, however deep");
        using var still = Registry.CurrentUser.OpenSubKey(_root);
        still!.GetSubKeyNames().Should().BeEquivalentTo(["mid", "open"], "nothing was deleted");
    }

    [Fact]
    public async Task A_recursive_delete_denied_part_way_says_some_sub_keys_may_already_be_gone()
    {
        // R25a. The count reads every key (nothing denies READ), so the tree delete starts; it
        // removes 'a' (enumerated first), then RegDeleteKeyEx on 'z\leaf' is denied (DELETE is
        // denied on leaf). Sub-keys are already gone — pre-existing and NOT fixed here (logged as a
        // separate defect) — so the message must not read as a clean refusal.
        Child("a");
        Child("z");
        var leaf = Child("z\\leaf");
        Deny(leaf, RegistryRights.Delete);

        var ex = await DeniedAsync(() => _svc.DeleteKeyAsync(Hive, _root, recursive: true), _root);

        ex.Message.Should().MatchRegex("(?i)part[- ]?way", "the delete was denied part-way");
        ex.Message.Should().MatchRegex("(?i)sub-?keys? may already have been (removed|deleted)",
            "and the caller is told the tree may be half gone");
        using var still = Registry.CurrentUser.OpenSubKey(leaf);
        still.Should().NotBeNull("precondition: the denied key itself survived");
    }

    // ---- a key the caller may read but not WRITE / DELETE ------------------------------------

    private const RegistryRights NoWrite = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete;

    [Fact]
    public async Task Delete_value_on_a_key_the_caller_may_not_write_is_an_UnauthorizedAccessException_and_keeps_the_value()
    {
        Deny(_root, NoWrite);

        await DeniedAsync(() => _svc.DeleteValueAsync(Hive, _root, "keep"), _root);

        using var key = Registry.CurrentUser.OpenSubKey(_root);
        key!.GetValue("keep").Should().Be("value");
    }

    [Fact]
    public async Task Set_on_a_key_the_caller_may_not_write_is_an_UnauthorizedAccessException_naming_it()
    {
        // CreateSubKey already throws UnauthorizedAccessException naming the full key; pinned so
        // the family stays one shape.
        Deny(_root, NoWrite);

        await DeniedAsync(() => _svc.SetAsync(Hive, _root, "new", "x", "String"), _root);
    }

    [Fact]
    public async Task Delete_key_on_a_key_the_caller_may_read_but_not_delete_is_an_UnauthorizedAccessException_naming_it()
    {
        // DeleteSubKey reports ERROR_ACCESS_DENIED as a BARE UnauthorizedAccessException
        // ("Attempted to perform an unauthorized operation.") - caller-facing but naming nothing.
        var leaf = Child("leaf");
        Deny(leaf, RegistryRights.Delete);

        await DeniedAsync(() => _svc.DeleteKeyAsync(Hive, leaf, recursive: false), leaf);

        using var key = Registry.CurrentUser.OpenSubKey(leaf);
        key.Should().NotBeNull("the key was not deleted");
    }
}
