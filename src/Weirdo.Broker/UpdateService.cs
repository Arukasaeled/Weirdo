using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weirdo.Core;
using Weirdo.Core.Services;
namespace Weirdo.BrokerHost;
internal static class UpdateService
{
    private static string CurrentVersion=>AppInfo.Version;
    private sealed record Manifest(string Version,string Url,string Sha256,string? Notes);
    private static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(90)};
    private static readonly StateStore State=new(AppPaths.Data);
    public static JsonObject Configure(string feedUrl)
    {
        feedUrl=feedUrl.Trim();
        if(feedUrl!=""&&!Path.IsPathFullyQualified(feedUrl)&&(!Uri.TryCreate(feedUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"))throw new ArgumentException("Use an HTTPS manifest URL or an absolute local JSON file path.");
        State.Save("updates",new JsonObject { ["feedUrl"] = feedUrl });return new JsonObject { ["configured"] = feedUrl != "", ["feedUrl"] = feedUrl };
    }
    private static string? Feed()=>State.Load("updates")?.GetProperty("feedUrl").GetString();
    private static string InstallerCache()
    {
        var sid=WindowsIdentity.GetCurrent().User??throw new InvalidOperationException("Windows user identity is unavailable.");
        var bucket=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid.Value)))[..16];
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Weirdo","Updates",bucket);
        var directory=Directory.CreateDirectory(path);
        if((directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("The installer cache cannot be a redirected directory.");
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        foreach(var identity in new[]{sid,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            security.AddAccessRule(new FileSystemAccessRule(identity,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        directory.SetAccessControl(security);
        return path;
    }
    private static async Task<Manifest> Read(string feed)
    {
        var text=Path.IsPathFullyQualified(feed)?await File.ReadAllTextAsync(feed):await Http.GetStringAsync(feed);
        if(text.Length>512_000)throw new InvalidDataException("Update manifest is too large.");
        var json=JsonNode.Parse(text)?.AsObject()??throw new InvalidDataException("Invalid update manifest.");
        var manifest=new Manifest(json["version"]?.GetValue<string>() ?? "", json["url"]?.GetValue<string>() ?? "", json["sha256"]?.GetValue<string>() ?? "", json["notes"]?.GetValue<string>());
        if(!Version.TryParse(manifest.Version,out _)||manifest.Sha256.Length!=64||!manifest.Sha256.All(Uri.IsHexDigit))throw new InvalidDataException("Manifest needs version, URL and SHA-256.");
        if(!Path.IsPathFullyQualified(manifest.Url)&&(!Uri.TryCreate(manifest.Url,UriKind.Absolute,out var u)||u.Scheme!="https"))throw new InvalidDataException("Installer must use HTTPS or an absolute local path.");
        if(!Path.IsPathFullyQualified(feed)&&Path.IsPathFullyQualified(manifest.Url))throw new InvalidDataException("A remote manifest cannot launch local files.");
        return manifest;
    }
    public static async Task<JsonObject> CheckAsync()
    {
        var feed=Feed();
        if(string.IsNullOrWhiteSpace(feed))return new JsonObject { ["configured"] = false, ["available"] = false, ["currentVersion"] = CurrentVersion };
        var m=await Read(feed);return new JsonObject { ["configured"] = true, ["available"] = Version.Parse(m.Version) > Version.Parse(CurrentVersion), ["currentVersion"] = CurrentVersion, ["version"] = m.Version, ["notes"] = m.Notes, ["feedUrl"] = feed };
    }
    public static async Task<JsonObject> InstallAsync(int hostPid,Func<Task> reserve,Func<Task> cancel,Func<Task> shutdown)
    {
        var feed=Feed();if(string.IsNullOrWhiteSpace(feed))throw new InvalidOperationException("Configure a release feed first.");
        var m=await Read(feed);if(Version.Parse(m.Version)<=Version.Parse(CurrentVersion))throw new InvalidOperationException("You already have this version or a newer one.");
        var extension=Path.GetExtension(new Uri(Path.IsPathFullyQualified(m.Url)?new Uri(m.Url).AbsoluteUri:m.Url).AbsolutePath);
        if(extension is not ".msi" and not ".exe")throw new InvalidDataException("Update must be an MSI or EXE installer.");
        var directory=Path.Combine(AppPaths.Data,"updates");Directory.CreateDirectory(directory);
        var target=Path.Combine(InstallerCache(),"Weirdo-"+m.Version+"-"+m.Sha256[..12]+extension);var partial=target+".partial";
        try
        {
            if(Path.IsPathFullyQualified(m.Url))
            {
                // Copy public installer bytes, not EFS attributes from the source.
                await using var input=File.OpenRead(m.Url);await using var output=File.Create(partial);
                await input.CopyToAsync(output);await output.FlushAsync();
            }
            else
            {
                using var response=await Http.GetAsync(m.Url,HttpCompletionOption.ResponseHeadersRead);response.EnsureSuccessStatusCode();
                if(response.Content.Headers.ContentLength is > 512_000_000)throw new InvalidDataException("Installer exceeds 512 MB.");
                await using var input=await response.Content.ReadAsStreamAsync();await using var output=File.Create(partial);
                var buffer=new byte[65536];long total=0;int count;
                while((count=await input.ReadAsync(buffer))>0){total+=count;if(total>512_000_000)throw new InvalidDataException("Installer exceeds 512 MB.");await output.WriteAsync(buffer.AsMemory(0,count));}
            }
            if((File.GetAttributes(partial)&FileAttributes.Encrypted)!=0)throw new IOException("Windows Installer cannot read an encrypted package cache. The installation was not started.");
            await using(var stream=File.OpenRead(partial)){var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream));if(!hash.Equals(m.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Update SHA-256 verification failed. Installer was not run.");}
            File.Move(partial,target,true);
        }
        finally{if(File.Exists(partial))File.Delete(partial);}
        // An OS-provided helper releases every installed Weirdo binary before MSI runs.
        static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
        // Use framework APIs rather than auto-loaded cmdlets: a parent PowerShell 7
        // can supply a PSModulePath that Windows PowerShell 5 cannot resolve.
        var script="$ErrorActionPreference='Stop'; try { foreach ($weirdoUpdatePid in @("+Environment.ProcessId+","+hostPid+")) { $weirdoUpdateProcess=$null; try { $weirdoUpdateProcess=[Diagnostics.Process]::GetProcessById($weirdoUpdatePid) } catch [ArgumentException] {}; if ($null -ne $weirdoUpdateProcess) { try { if (!$weirdoUpdateProcess.WaitForExit(40000)) { throw 'Weirdo did not exit before update' } } finally { $weirdoUpdateProcess.Dispose() } } }; "+
            "$weirdoHash=[Security.Cryptography.SHA256]::Create(); $weirdoStream=[IO.File]::OpenRead("+Quote(target)+"); try { $weirdoDigest=[BitConverter]::ToString($weirdoHash.ComputeHash($weirdoStream)).Replace('-','') } finally { $weirdoStream.Dispose(); $weirdoHash.Dispose() }; if ($weirdoDigest -ne "+Quote(m.Sha256)+") { throw 'Installer checksum changed' }; "+
            "$weirdoInstaller=[Diagnostics.ProcessStartInfo]::new(); $weirdoInstaller.UseShellExecute=$true; $weirdoInstaller.FileName="+Quote(extension==".msi"?"msiexec.exe":target)+"; "+
            (extension==".msi"?"$weirdoInstaller.Arguments="+Quote("/i \""+target+"\" /norestart /L*v \""+Path.Combine(Path.GetDirectoryName(target)!,"install-"+m.Version+".log")+"\"")+"; ":"")+
            "$weirdoInstalledProcess=[Diagnostics.Process]::Start($weirdoInstaller); if($null -eq $weirdoInstalledProcess){throw 'The installer did not start'}; try {$weirdoInstalledProcess.WaitForExit();if($weirdoInstalledProcess.ExitCode -notin @(0,3010)){throw ('Installer failed with exit code '+$weirdoInstalledProcess.ExitCode)}} finally {$weirdoInstalledProcess.Dispose()} } catch { [IO.File]::WriteAllText("+Quote(Path.Combine(directory,"update-error.txt"))+",$_.Exception.Message,[Text.Encoding]::UTF8) }";
        var helper=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell/v1.0/powershell.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        helper.ArgumentList.Add("-NoProfile");helper.ArgumentList.Add("-EncodedCommand");helper.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        await reserve();
        try
        {
            var priorError=Path.Combine(directory,"update-error.txt");
            if(File.Exists(priorError))File.Delete(priorError);
            Process.Start(helper)?.Dispose();await shutdown();
        }
        catch{await cancel();throw;}
        return new JsonObject { ["started"] = true, ["version"] = m.Version };
    }
}
