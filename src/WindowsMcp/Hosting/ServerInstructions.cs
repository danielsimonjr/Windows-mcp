namespace WindowsMcp.Hosting;

/// <summary>
/// E-1: what every client is told in the <c>initialize</c> result, on both transports - the
/// snapshot -> act -> confirm loop and the safety rails, condensed from
/// <c>skills/windows/SKILL.md</c>, which only a Claude Code plugin install ever sees.
/// <para>
/// Rules the tests enforce (<c>ServerInstructionsTests</c>): at most 2 000 characters (it is paid
/// for in every session, and clients cut what they keep at about 2 KB); printable ASCII with
/// explicit <c>\n</c> breaks - never a multi-line literal, whose breaks would follow the source
/// file's CRLF; and a back-ticked bare lowercase word is always a tool name, checked against the
/// discovered set, so parameters and fields are written without back-ticks.
/// </para>
/// <para>
/// Every sentence is a claim about the code, repeated to every client in every session: when a
/// tool's parameters, its confirm gate or what evicts element ids changes, change the sentence.
/// </para>
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "Windows-mcp drives a Windows desktop and inspects the machine. Prefer its tools over `powershell`.\n" +
        "\n" +
        "UI loop: read, act, confirm, re-read.\n" +
        "1. `snapshot` first: it lists the windows and every interactive control as el_N (x,y) type \"name\" [action: ...]. Labels and plain text are not rows: use `find_element` with kind:text or `wait_for` on text. On a busy desktop narrow it with scope and window; add use_dom:true for a page in a Chromium browser.\n" +
        "2. Act by id, not by coordinates: `interact_element` acts on an el_N id; `click`, `type` and `scroll` take element_id; `drag` goes from from_element_id to element_id. `type` with clear and press_enter fills a field in one call.\n" +
        "3. Confirm with `assert_element` or `wait_for` (it polls a condition; a timeout is Satisfied:false, not an error). `wait` is only for what cannot be observed.\n" +
        "4. Snapshot ids die at the next `snapshot`, and at `screenshot` with annotate:true, `wait_for` on text or focus, and `scrape` with source dom. Re-snapshot after acting; never reuse old rows.\n" +
        "`screenshot` returns the image inline; read its metadata before using its pixels as coordinates.\n" +
        "\n" +
        "Rules:\n" +
        "- Truncated in a result means there is more: narrow the request before concluding something is absent.\n" +
        "- Error text is the server's answer. Act on it instead of retrying unchanged. Only \"An error occurred invoking ...\" is an unexpected fault.\n" +
        "- A result ending \"Note: the user pressed ... and took over the mouse and keyboard\" means the person took control back. Stop sending input, tell them, and `snapshot` again before continuing.\n" +
        "- Some changes are refused without confirm: true. Others never ask: `powershell`, `archive`, `file_manage` with overwrite, `scheduled_task` create or run, `service` start, `job` cancel, `window` close, `http_request`. Get the user's agreement before anything destructive or persistent, gated or not.\n" +
        "- The server usually runs unelevated: report access-denied, do not retry.\n" +
        "- Long scripts: `powershell` with background:true, then `job`.";
}
