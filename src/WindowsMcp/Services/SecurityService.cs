using System.Text.Json;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class SecurityService : ISecurityService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IPowerShellService _ps;

    public SecurityService(IPowerShellService ps) => _ps = ps;

    public async Task<SecurityAuditDto> AuditAsync(CancellationToken ct = default)
    {
        var result = await _ps.RunAsync(AuditScript, ct);
        var stdout = result.Stdout?.Trim();

        // Empty output means every probe failed (typically no admin) — return a typed result
        // with a note rather than a blank string, so callers always get a parseable shape.
        if (string.IsNullOrEmpty(stdout))
            return new SecurityAuditDto(null, null, null, null, "all probes failed; likely no admin");

        return JsonSerializer.Deserialize<SecurityAuditDto>(stdout, JsonOpts)
            ?? new SecurityAuditDto(null, null, null, null, "audit returned unparseable output");
    }

    public async Task<DefenderStatusDto> GetDefenderStatusAsync(CancellationToken ct = default)
    {
        var result = await _ps.RunAsync(DefenderScript, ct);
        var stdout = result.Stdout?.Trim();
        if (string.IsNullOrEmpty(stdout))
            return new DefenderStatusDto(null, null, null, null, null, null, null, null,
                "Defender status unavailable (Get-MpComputerStatus failed; Defender may be disabled or replaced by a third-party AV)");

        try
        {
            return JsonSerializer.Deserialize<DefenderStatusDto>(stdout, JsonOpts)
                ?? new DefenderStatusDto(null, null, null, null, null, null, null, null, "unparseable Defender status output");
        }
        catch (JsonException ex)
        {
            // Don't let an unexpected JSON shape (e.g. a legacy date format) fault the whole tool.
            return new DefenderStatusDto(null, null, null, null, null, null, null, null,
                "could not parse Defender status: " + ex.Message);
        }
    }

    // Single pipeline (survives the -Command - stdin path). Property names match DefenderStatusDto.
    // DateTime fields are forced to ISO 8601 via .ToString('o'): Windows PowerShell 5.1 otherwise
    // serializes them as "\/Date(ms)\/", which System.Text.Json cannot parse into DateTime?.
        // The `else{$null}` below is NOT redundant. A calculated property whose scriptblock emits
        // NOTHING serializes as an empty OBJECT {} - not null - and {} cannot convert to DateTime?,
        // which fails the WHOLE DefenderStatusDto and blanks EVERY field. Found on EVO-X2
        // 2026-08-23: a machine that had never completed a full scan returned
        // "could not be converted ... Path: $.FullScanEndTime" and reported nothing, while
        // security_audit independently confirmed Defender was RUNNING. A tool that renders a parse
        // failure as an all-null security posture is more dangerous than one that errors outright.
    private const string DefenderScript =
        "Get-MpComputerStatus | Select-Object RealTimeProtectionEnabled,AntivirusEnabled,IsTamperProtected," +
        "BehaviorMonitorEnabled,AntivirusSignatureVersion," +
        "@{n='AntivirusSignatureLastUpdated';e={if($_.AntivirusSignatureLastUpdated){$_.AntivirusSignatureLastUpdated.ToString('o')}else{$null}}}," +
        "@{n='QuickScanEndTime';e={if($_.QuickScanEndTime){$_.QuickScanEndTime.ToString('o')}else{$null}}}," +
        "@{n='FullScanEndTime';e={if($_.FullScanEndTime){$_.FullScanEndTime.ToString('o')}else{$null}}} " +
        "| ConvertTo-Json";

    // Each probe is isolated in its own try/catch so one missing cmdlet or permission failure
    // doesn't blank the whole report. Keys are PascalCase to match SecurityAuditDto.
    private const string AuditScript = @"
$result = [ordered]@{
  FirewallEnabled = $null
  DefenderRunning = $null
  UacLevel        = $null
  BitlockerStatus = $null
}
try { $result.FirewallEnabled = [bool]((Get-NetFirewallProfile -ErrorAction Stop | Where-Object Enabled).Count -gt 0) } catch {}
try { $result.DefenderRunning = ((Get-Service WinDefend -ErrorAction Stop).Status -eq 'Running') } catch {}
try { $result.UacLevel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction Stop).ConsentPromptBehaviorAdmin } catch {}
try { $result.BitlockerStatus = (Get-BitLockerVolume -MountPoint C: -ErrorAction Stop).ProtectionStatus.ToString() } catch {}
$result | ConvertTo-Json -Compress
";
}
