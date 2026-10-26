using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

sealed class OpenCodeInstallRequiredException : InvalidOperationException
{
    public OpenCodeInstallRequiredException(string message) : base(message) { }
}

sealed class OpenCodeInstallation
{
    public string ApiVersion { get; set; }
    public string Version { get; set; }
    public string Executable { get; set; }
    public bool IsRunning { get; set; }
    public bool IsBundled { get; set; }
    public string DisplayName
    {
        get { return "OpenCode "+Version+" (API "+ApiVersion+")"+(IsRunning?" — running":"")+Environment.NewLine+Executable; }
    }
    public override string ToString() { return DisplayName; }
}

[DataContract]
sealed class BunReleaseInfo
{
    [DataMember(Name="assets")] public List<BunReleaseAsset> Assets { get; set; }
}
[DataContract]
sealed class BunReleaseAsset
{
    [DataMember(Name="name")] public string Name { get; set; }
    [DataMember(Name="browser_download_url")] public string DownloadUrl { get; set; }
    [DataMember(Name="digest")] public string Digest { get; set; }
    [DataMember(Name="size")] public long Size { get; set; }
}

static class BotRuntime
{
    static readonly Dictionary<string,string> dependencyCache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    public static string OfflineRoot { get { return Path.Combine(ProductPaths.Root,"dependencies"); } }
    public static void ConfigureFixedWebViewPermissions(string installRoot)
    {
        string folder=Path.Combine(installRoot,"dependencies","webview2");
        if(!Directory.Exists(folder))return;
        // Fixed WebView2's Windows 10 sandbox requires read/execute access for
        // application packages. These grants affect only the app-local runtime.
        var info=new ProcessStartInfo{FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"icacls.exe"),Arguments="\""+folder+"\" /grant *S-1-15-2-1:(OI)(CI)(RX) *S-1-15-2-2:(OI)(CI)(RX) /T /Q",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        using(var process=Process.Start(info))
        {
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(60000)){try{process.Kill();}catch{}throw new TimeoutException("Fixed WebView2 permissions setup timed out.");}
            Task.WaitAll(stdout,stderr);
            if(process.ExitCode!=0)throw new IOException("Fixed WebView2 permissions setup failed: "+stderr.Result);
        }
    }
    static string CompatibleTool(string executable,Version minimum)
    {
        if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable))return "";
        try
        {
            var match=Regex.Match(RunAndReadOutput(executable,"--version",5000),@"\d+\.\d+(?:\.\d+)?");
            Version version;
            return match.Success&&Version.TryParse(match.Value,out version)&&version>=minimum?executable:"";
        }
        catch{return "";}
    }
    static string ResolveGitExecutable()
    {
        string key="git:"+ProductPaths.Root;
        lock(dependencyCache)
        {
            string selected;if(dependencyCache.TryGetValue(key,out selected))return selected;
            var candidates=GetSearchPathEntries().Select(directory=>Path.Combine(directory,"git.exe")).Concat(new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Git","cmd","git.exe"),Path.Combine(OfflineRoot,"git","cmd","git.exe")});
            selected=candidates.Select(candidate=>CompatibleTool(candidate,new Version(2,30))).FirstOrDefault(candidate=>!String.IsNullOrEmpty(candidate))??"";
            dependencyCache[key]=selected;return selected;
        }
    }
    public static string ResolveWebViewRuntime()
    {
        try
        {
            string available=Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            if(ParseOpenCodeVersion(available)>=new Version(154,0,4258))return null;
        }
        catch(Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
        }
        string folder=Path.Combine(OfflineRoot,"webview2");
        if(!File.Exists(Path.Combine(folder,"msedgewebview2.exe")))throw new FileNotFoundException("Bundled WebView2 runtime is missing or installed runtime is too old.");
        return folder;
    }
    const string DefaultV1ApiUrl = "http://127.0.0.1:4096";
    const string DefaultV2ApiUrl = "http://127.0.0.1:49374";
    static string DefaultApiUrl(string version) { return DefaultV2ApiUrl; }
    public static void EnsureLocalV2Password(DesktopSettings settings)
    {
        if(settings.OpenCodeServerVersion!="v2"||!IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings))||!String.IsNullOrWhiteSpace(settings.OpenCodePassword)) return;
        byte[] secret=new byte[32];
        using(var random=RandomNumberGenerator.Create()) random.GetBytes(secret);
        settings.OpenCodePassword=BitConverter.ToString(secret).Replace("-","").ToLowerInvariant();
        Array.Clear(secret,0,secret.Length);
        SettingsStore.Save(settings);
    }
    public static string ResolveOpenCodeExecutable(DesktopSettings settings)
    {
        string version="v2";
        string engineRoot=Path.GetFullPath(Path.Combine(ProductPaths.Home,"opencode-runtime"));
        string configured=String.IsNullOrWhiteSpace(settings.OpenCodeExecutable)?"":Path.GetFullPath(settings.OpenCodeExecutable.Trim());
        string selected=Path.Combine(engineRoot,version,"node_modules",".bin","opencode.cmd");
        if(!String.IsNullOrWhiteSpace(configured))
        {
            bool isManaged=configured.StartsWith(engineRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
            string offlineEngineRoot=Path.GetFullPath(Path.Combine(OfflineRoot,"opencode"))+Path.DirectorySeparatorChar;
            if(configured.StartsWith(offlineEngineRoot,StringComparison.OrdinalIgnoreCase))
            {
                string packaged=Path.Combine(OfflineRoot,"opencode",version,"opencode.exe");
                return File.Exists(packaged)?packaged:"";
            }
            if(File.Exists(configured)) return configured;
        }
        if(File.Exists(selected))return selected;
        string bundled=Path.Combine(OfflineRoot,"opencode",version,"opencode.exe");
        if(!File.Exists(bundled))return "";
        string key="opencode:"+ProductPaths.Root+":"+ProductPaths.Home+":"+version;
        lock(dependencyCache)
        {
            string existing;
            if(!dependencyCache.TryGetValue(key,out existing))
            {
                try { existing=ScanOpenCodeInstallations(settings).Where(item=>item.ApiVersion==version).Select(item=>item.Executable).FirstOrDefault()??bundled; }
                catch { existing=bundled; }
                dependencyCache[key]=existing;
            }
            return existing;
        }
    }
    public static string BuildOpenCodePath(DesktopSettings settings,string executableOverride=null)
    {
        string executable=String.IsNullOrWhiteSpace(executableOverride)?ResolveOpenCodeExecutable(settings):executableOverride;
        string executableDirectory=String.IsNullOrWhiteSpace(executable)?"":Path.GetDirectoryName(executable);
        string bun=ResolveBunExecutable();string bunDirectory=String.IsNullOrWhiteSpace(bun)?"":Path.GetDirectoryName(bun);
        string git=ResolveGitExecutable();string gitDirectory=String.IsNullOrWhiteSpace(git)?"":Path.GetDirectoryName(git);
        string gitBash=String.IsNullOrWhiteSpace(gitDirectory)?"":Path.GetFullPath(Path.Combine(gitDirectory,"..","bin"));
        if(!File.Exists(Path.Combine(gitBash,"bash.exe")))gitBash="";
        return executableDirectory+Path.PathSeparator+Path.Combine(ProductPaths.Root,"bin")+Path.PathSeparator+ProductPaths.BotModulesBin+Path.PathSeparator+bunDirectory+Path.PathSeparator+Path.GetDirectoryName(ProductPaths.Node)+Path.PathSeparator+gitDirectory+Path.PathSeparator+gitBash+Path.PathSeparator+(Environment.GetEnvironmentVariable("PATH")??"");
    }
    static string ReadOpenCodeVersion(DesktopSettings settings,string executableOverride=null)
    {
        Directory.CreateDirectory(ProductPaths.Home);
        var info=new ProcessStartInfo { FileName=Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe", Arguments="/d /c opencode --version", WorkingDirectory=ProductPaths.Home, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        info.EnvironmentVariables["PATH"]=BuildOpenCodePath(settings,executableOverride);
        using(var process=Process.Start(info))
        {
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(20000)){try{process.Kill();}catch{}throw new TimeoutException("Timed out checking the selected OpenCode CLI version.");}
            Task.WaitAll(stdout,stderr);
            string output=(stdout.Result+Environment.NewLine+stderr.Result).Trim();
            if(process.ExitCode!=0) throw new InvalidOperationException(String.IsNullOrWhiteSpace(output)?"OpenCode CLI was not found on PATH.":output);
            return output;
        }
    }
    static int ReadOpenCodeMajorVersion(string output)
    {
        var match=Regex.Match(output??"",@"(\d+)\.\d+\.\d+");
        int major;
        return match.Success&&Int32.TryParse(match.Groups[1].Value,out major)?major:0;
    }
    static string ReadOpenCodeVersionFromExecutable(string executable)
    {
        Directory.CreateDirectory(ProductPaths.Home);
        string extension=Path.GetExtension(executable);
        var info=new ProcessStartInfo { WorkingDirectory=ProductPaths.Home, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        if(String.Equals(extension,".cmd",StringComparison.OrdinalIgnoreCase)||String.Equals(extension,".bat",StringComparison.OrdinalIgnoreCase))
        {
            info.FileName=Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe";
            info.Arguments="/d /s /c \"\""+executable+"\" --version\"";
        }
        else
        {
            info.FileName=executable;
            info.Arguments="--version";
        }
        using(var process=Process.Start(info))
        {
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(20000)){try{process.Kill();}catch{}throw new TimeoutException("Timed out checking "+executable);}
            Task.WaitAll(stdout,stderr);
            string output=(stdout.Result+Environment.NewLine+stderr.Result).Trim();
            if(process.ExitCode!=0) throw new InvalidOperationException(output);
            return output;
        }
    }
    static Version ParseOpenCodeVersion(string output)
    {
        var match=Regex.Match(output??"",@"(\d+\.\d+\.\d+)");
        Version version;
        return match.Success&&Version.TryParse(match.Groups[1].Value,out version)?version:new Version(0,0,0);
    }
    static IEnumerable<string> GetSearchPathEntries()
    {
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(EnvironmentVariableTarget target in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine})
            foreach(string entry in (Environment.GetEnvironmentVariable("PATH",target)??"").Split(Path.PathSeparator))
                if(!String.IsNullOrWhiteSpace(entry)) paths.Add(entry.Trim().Trim('"'));
        return paths;
    }
    static string RunAndReadOutput(string executable,string arguments,int timeoutMs)
    {
        var info=new ProcessStartInfo { FileName=executable, Arguments=arguments, WorkingDirectory=ProductPaths.Home, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        using(var process=Process.Start(info))
        {
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(timeoutMs)){try{process.Kill();}catch{}throw new TimeoutException("Timed out running "+Path.GetFileName(executable)+".");}
            Task.WaitAll(stdout,stderr);
            if(process.ExitCode!=0) throw new InvalidOperationException((stdout.Result+Environment.NewLine+stderr.Result).Trim());
            return stdout.Result.Trim();
        }
    }
    public static List<OpenCodeInstallation> ScanOpenCodeInstallations(DesktopSettings settings)
    {
        Directory.CreateDirectory(ProductPaths.Home);
        var candidates=new List<string>();
        var running=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Action<string,bool> add=(value,isRunning)=>
        {
            if(String.IsNullOrWhiteSpace(value)) return;
            try
            {
                string fullPath=Path.GetFullPath(value.Trim());
                if(String.Equals(fullPath,Path.Combine(ProductPaths.Root,"bin","opencode.cmd"),StringComparison.OrdinalIgnoreCase))return;
                if(!candidates.Contains(fullPath,StringComparer.OrdinalIgnoreCase)) candidates.Add(fullPath);
                if(isRunning) running.Add(fullPath);
            }
            catch { }
        };
        add(settings.OpenCodeExecutable,false);
        add(Path.Combine(OfflineRoot,"opencode","v2","opencode.exe"),false);
        string updates=Path.Combine(ProductPaths.Home,"opencode-runtime","v2","versions");
        if(Directory.Exists(updates))foreach(string folder in Directory.GetDirectories(updates))add(Path.Combine(folder,"node_modules",".bin","opencode.cmd"),false);
        foreach(string version in new[]{"v2"}) add(Path.Combine(ProductPaths.Home,"opencode-runtime",version,"node_modules",".bin","opencode.cmd"),false);
        foreach(string directory in GetSearchPathEntries())
        {
            add(Path.Combine(directory,"opencode.exe"),false);
            add(Path.Combine(directory,"opencode.cmd"),false);
        }
        string localAppData=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach(string candidate in new[]
        {
            Path.Combine(appData,"npm","opencode.cmd"),
            Path.Combine(appData,"npm","opencode.exe"),
            Path.Combine(appData,"npm","node_modules","opencode-ai","bin","opencode.exe"),
            Path.Combine(appData,"npm","node_modules","@opencode","cli","bin","opencode.exe"),
            Path.Combine(userProfile,".bun","bin","opencode.exe"),
            Path.Combine(userProfile,".bun","install","global","node_modules","opencode-ai","bin","opencode.exe"),
            Path.Combine(userProfile,".bun","install","global","node_modules","@opencode","cli","bin","opencode.exe"),
            Path.Combine(userProfile,"scoop","apps","opencode","current","opencode.exe"),
            Path.Combine(userProfile,"scoop","apps","opencode","current","bin","opencode.exe"),
            Path.Combine(localAppData,"Programs","opencode","opencode.exe"),
            Path.Combine(localAppData,"Programs","OpenCode","opencode.exe"),
            Path.Combine(localAppData,"opencode","opencode.exe"),
            Path.Combine(localAppData,"Microsoft","WinGet","Links","opencode.exe"),
            Path.Combine(programFiles,"OpenCode","opencode.exe"),
            Path.Combine(programFilesX86,"OpenCode","opencode.exe"),
        }) add(candidate,false);
        string node=ResolveNodeExecutable();
        if(!String.IsNullOrWhiteSpace(node)&&File.Exists(ProductPaths.NpmCli))
        {
            try
            {
                string prefix=RunAndReadOutput(node,"\""+ProductPaths.NpmCli+"\" config get prefix",15000);
                add(Path.Combine(prefix,"opencode.cmd"),false);
                add(Path.Combine(prefix,"opencode.exe"),false);
                add(Path.Combine(prefix,"node_modules","opencode-ai","bin","opencode.exe"),false);
                add(Path.Combine(prefix,"node_modules","@opencode","cli","bin","opencode.exe"),false);
            }
            catch { }
        }
        string bun=ResolveBunExecutable();
        if(!String.IsNullOrWhiteSpace(bun))
        {
            try
            {
                string bunBin=RunAndReadOutput(bun,"pm bin -g",15000);
                string globalModules=Path.GetFullPath(Path.Combine(bunBin,"..","install","global","node_modules"));
                add(Path.Combine(bunBin,"opencode.exe"),false);
                add(Path.Combine(bunBin,"opencode.cmd"),false);
                add(Path.Combine(globalModules,"opencode-ai","bin","opencode.exe"),false);
                add(Path.Combine(globalModules,"@opencode","cli","bin","opencode.exe"),false);
            }
            catch { }
        }
        foreach(string keyPath in new[]{@"Software\Microsoft\Windows\CurrentVersion\App Paths\opencode.exe",@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\opencode.exe"})
        {
            foreach(RegistryKey hive in new[]{Registry.CurrentUser,Registry.LocalMachine})
            {
                using(var key=hive.OpenSubKey(keyPath,false))
                    if(key!=null) add(key.GetValue(null) as string,false);
            }
        }
        foreach(RegistryKey hive in new[]{Registry.CurrentUser,Registry.LocalMachine})
        foreach(string uninstallPath in new[]{@"Software\Microsoft\Windows\CurrentVersion\Uninstall",@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"})
        using(var uninstall=hive.OpenSubKey(uninstallPath,false))
        {
            if(uninstall==null) continue;
            foreach(string subkeyName in uninstall.GetSubKeyNames())
            using(var entry=uninstall.OpenSubKey(subkeyName,false))
            {
                if(entry==null||((entry.GetValue("DisplayName") as string??"").IndexOf("OpenCode",StringComparison.OrdinalIgnoreCase)<0)) continue;
                string installLocation=entry.GetValue("InstallLocation") as string;
                if(String.IsNullOrWhiteSpace(installLocation)) continue;
                add(Path.Combine(installLocation,"opencode.exe"),false);
                add(Path.Combine(installLocation,"opencode.cmd"),false);
                add(Path.Combine(installLocation,"bin","opencode.exe"),false);
            }
        }
        using(var search=new ManagementObjectSearcher("SELECT ProcessId, Name, ExecutablePath FROM Win32_Process"))
        using(var processes=search.Get())
        foreach(ManagementObject process in processes)
        {
            try
            {
                string name=process["Name"] as string??"";
                string executable=process["ExecutablePath"] as string;
                bool isOpenCode=String.Equals(Path.GetFileNameWithoutExtension(name),"opencode",StringComparison.OrdinalIgnoreCase);
                if(isOpenCode&&!String.IsNullOrWhiteSpace(executable)) add(executable,true);
            }
            catch(ManagementException) { }
        }

        var detected=new List<OpenCodeInstallation>();
        foreach(string executable in candidates)
        {
            if(!File.Exists(executable)) continue;
            try
            {
                string output=ReadOpenCodeVersionFromExecutable(executable);
                int major=ReadOpenCodeMajorVersion(output);
                if(major!=2) continue;
                detected.Add(new OpenCodeInstallation
                {
                    ApiVersion="v2",
                    Version=ParseOpenCodeVersion(output).ToString(),
                    Executable=executable,
                    IsRunning=running.Contains(executable),
                    IsBundled=Path.GetFullPath(executable).StartsWith(Path.GetFullPath(Path.Combine(OfflineRoot,"opencode"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),
                });
            }
            catch { }
        }

        string configuredPath="";
        try { if(!String.IsNullOrWhiteSpace(settings.OpenCodeExecutable)) configuredPath=Path.GetFullPath(settings.OpenCodeExecutable); }
        catch { }
        return detected
            .GroupBy(item=>item.Executable,StringComparer.OrdinalIgnoreCase)
            .Select(group=>group.First())
            .OrderByDescending(item=>String.Equals(item.Executable,configuredPath,StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item=>item.IsRunning)
            .ThenBy(item=>item.ApiVersion)
            .ThenByDescending(item=>ParseOpenCodeVersion(item.Version))
            .ThenBy(item=>Path.GetExtension(item.Executable).Equals(".exe",StringComparison.OrdinalIgnoreCase)?0:1)
            .ToList();
    }
    public static bool IsLocalOpenCodeUrl(string value)
    {
        Uri uri;
        return Uri.TryCreate(value,UriKind.Absolute,out uri)&&(uri.IsLoopback||String.Equals(uri.Host,"0.0.0.0",StringComparison.OrdinalIgnoreCase));
    }
    static string InstallOpenCodeHint(DesktopSettings settings,string version)
    {
        return settings.Locale=="en"
            ? "Please open OpenCode Engine and click Install OpenCode (API "+version+") before starting the service."
            : "请前往“OpenCode 引擎”页面，点击“安装 OpenCode（API "+version+")”，安装完成后再启动服务。";
    }
    public static string GetInstalledOpenCodeVersion(DesktopSettings settings) { return ReadOpenCodeVersion(settings); }
    static void EnsureOpenCodeInstalled(DesktopSettings settings)
    {
        string version="v2";
        if(!IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings))) return;
        string output;
        try { output=ReadOpenCodeVersion(settings); }
        catch(Exception ex)
        {
            string message=settings.Locale=="en"?"OpenCode API "+version+" is not installed or available. ":"OpenCode API "+version+" 未安装或不可用。";
            throw new OpenCodeInstallRequiredException(message+InstallOpenCodeHint(settings,version)+Environment.NewLine+ex.Message);
        }
        int major=ReadOpenCodeMajorVersion(output);
        int expected=version=="v2"?2:1;
        if(major!=expected)
        {
            string message=settings.Locale=="en"
                ? "The selected API is "+version+", but the available OpenCode CLI is "+(String.IsNullOrWhiteSpace(output)?"unknown":output)+". "
                : "当前选择的 API 是"+version+"，但检测到 OpenCode CLI "+(String.IsNullOrWhiteSpace(output)?"unknown":output)+"。";
            throw new OpenCodeInstallRequiredException(message+InstallOpenCodeHint(settings,version));
        }
    }
    public static string ResolveOpenCodeUrl(DesktopSettings settings)
    {
        string version="v2";
        string configured=String.IsNullOrWhiteSpace(settings.OpenCodeUrl)?"":settings.OpenCodeUrl.Trim();
        if(String.IsNullOrWhiteSpace(configured)||configured==DefaultV1ApiUrl||configured==DefaultV2ApiUrl) return DefaultApiUrl(version);
        return configured;
    }
    public static Uri AddWebAuthToken(Uri address,DesktopSettings settings)
    {
        if(settings.OpenCodeServerVersion!="v2"||String.IsNullOrWhiteSpace(settings.OpenCodePassword)) return address;
        var builder=new UriBuilder(address);
        string token=Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:"+settings.OpenCodePassword));
        string query=builder.Query.TrimStart('?');
        string auth="auth_token="+Uri.EscapeDataString(token);
        builder.Query=String.IsNullOrWhiteSpace(query)?auth:query+"&"+auth;
        return builder.Uri;
    }
    public static Task<BrowserLoginRedirect> PrepareBrowserLogin(Uri target,DesktopSettings settings)
    {
        return Task.Run(()=>
        {
            if(!target.IsLoopback)throw new InvalidOperationException("Browser login redirect requires a local OpenCode service.");
            Uri configured=new Uri(ResolveOpenCodeUrl(settings));
            if(!String.Equals(target.GetLeftPart(UriPartial.Authority),configured.GetLeftPart(UriPartial.Authority),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Browser login target must match the configured OpenCode service.");
            Uri origin=new Uri(target.GetLeftPart(UriPartial.Authority)+"/");
            var pair=(HttpWebRequest)WebRequest.Create(new Uri(origin,"api/pair"));
            pair.Method="POST";pair.ContentLength=0;pair.Proxy=null;pair.Timeout=10000;pair.ReadWriteTimeout=10000;pair.AllowAutoRedirect=false;
            pair.Headers[HttpRequestHeader.Authorization]="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes((settings.OpenCodeUsername??"opencode")+":"+settings.OpenCodePassword));
            BrowserPairingCode code;
            using(var response=(HttpWebResponse)pair.GetResponse())
            using(var stream=response.GetResponseStream())
                code=(BrowserPairingCode)new DataContractJsonSerializer(typeof(BrowserPairingCode)).ReadObject(stream);
            if(code==null||String.IsNullOrWhiteSpace(code.Code))throw new InvalidOperationException("OpenCode did not return a browser pairing code.");
            var connect=(HttpWebRequest)WebRequest.Create(new Uri(origin,"auth/connect/"+Uri.EscapeDataString(code.Code)));
            connect.Accept="text/html";connect.AllowAutoRedirect=false;connect.Proxy=null;connect.Timeout=10000;connect.ReadWriteTimeout=10000;
            using(var response=(HttpWebResponse)connect.GetResponse())
            {
                var cookies=response.Headers.GetValues("Set-Cookie");
                if((response.StatusCode!=HttpStatusCode.Found&&response.StatusCode!=HttpStatusCode.SeeOther)||cookies==null||cookies.Length==0)throw new InvalidOperationException("OpenCode did not return a browser session cookie.");
                return new BrowserLoginRedirect(target,cookies);
            }
        });
    }
    public static async Task OpenExternalBrowser(Uri target,DesktopSettings settings)
    {
        if(settings.OpenCodeServerVersion=="v2"&&target.IsLoopback&&!String.IsNullOrWhiteSpace(settings.OpenCodePassword))
        {
            using(var login=await PrepareBrowserLogin(target,settings))
            {
                Process.Start(new ProcessStartInfo{FileName=login.Address.AbsoluteUri,UseShellExecute=true});
                await login.CompleteAsync();
            }
            return;
        }
        Process.Start(new ProcessStartInfo{FileName=AddWebAuthToken(target,settings).ToString(),UseShellExecute=true});
    }
    static string ResolveNativeOpenCodeExecutable(string executable)
    {
        if(!Path.GetExtension(executable).Equals(".cmd",StringComparison.OrdinalIgnoreCase)&&!Path.GetExtension(executable).Equals(".bat",StringComparison.OrdinalIgnoreCase)) return executable;
        try
        {
            string shim=File.ReadAllText(executable);
            Match match=Regex.Match(shim,@"(?i)""%~?dp0%?\\([^""]+?\.exe)""");
            if(!match.Success)return "";
            string relative=match.Groups[1].Value.Replace('\\',Path.DirectorySeparatorChar);
            string target=Path.Combine(Path.GetDirectoryName(executable),relative);
            return File.Exists(target)?target:"";
        }
        catch{return "";}
    }
    static string QuoteProcessArgument(string value)
    {
        return "\""+(value??"").Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
    }
    static string RunOpenCodeCommand(DesktopSettings settings,string executable,params string[] arguments)
    {
        string native=ResolveNativeOpenCodeExecutable(executable);
        if(String.IsNullOrWhiteSpace(native))throw new InvalidOperationException("Could not resolve the selected OpenCode CLI executable.");
        var info=new ProcessStartInfo{FileName=native,Arguments=String.Join(" ",arguments.Select(QuoteProcessArgument)),WorkingDirectory=ProductPaths.Home,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.EnvironmentVariables["PATH"]=BuildOpenCodePath(settings,executable);
        Directory.CreateDirectory(ProductPaths.Home);
        using(var process=Process.Start(info))
        {
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(30000)){try{process.Kill();}catch{}throw new TimeoutException("Timed out running the selected OpenCode CLI command.");}
            Task.WaitAll(stdout,stderr);
            string output=(stdout.Result+Environment.NewLine+stderr.Result).Trim();
            if(process.ExitCode!=0)throw new InvalidOperationException(String.IsNullOrWhiteSpace(output)?"OpenCode CLI command failed.":output);
            return output;
        }
    }
    /// <summary>
    /// Registers the V2 service with its manager and asks it to start, without waiting for it
    /// to answer. The Bot can boot in parallel; call <see cref="VerifyRunningV2Service"/> after.
    /// </summary>
    public static Uri PrepareLocalV2Service(DesktopSettings settings)
    {
        string executable=RequireV2Executable(settings);
        ConfigureLocalV2ServiceRegistration(settings,executable);
        return StartPreparedLocalV2Service(settings,executable);
    }
    static string RequireV2Executable(DesktopSettings settings)
    {
        if(settings.OpenCodeServerVersion!="v2"||!IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings)))throw new InvalidOperationException("A local OpenCode V2 URL is required to start the managed V2 service.");
        EnsureLocalV2Password(settings);
        string executable=ResolveOpenCodeExecutable(settings);
        if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable))throw new OpenCodeInstallRequiredException(InstallOpenCodeHint(settings,"v2"));
        return executable;
    }
    /// <summary>The quick part: endpoint, password and username registration (a few CLI calls).</summary>
    static void ConfigureLocalV2ServiceRegistration(DesktopSettings settings,string executable)
    {
        RunOpenCodeCommand(settings,executable,"service","unset","disabled");
        Uri requested=new Uri(ResolveOpenCodeUrl(settings));
        if(!String.IsNullOrWhiteSpace(settings.OpenCodeUrl)&&settings.OpenCodeUrl!=DefaultV1ApiUrl&&settings.OpenCodeUrl!=DefaultV2ApiUrl)
        {
            string configuredPort=RunOpenCodeCommand(settings,executable,"service","get","port").Trim();
            if(configuredPort!=requested.Port.ToString())RunOpenCodeCommand(settings,executable,"service","set","port",requested.Port.ToString());
        }

        // V2's service manager owns the persistent password and endpoint registration.
        // Configure it before start so the CLI, Bot and browser all use one credential.
        string registeredPassword=RunOpenCodeCommand(settings,executable,"service","get","password").Trim();
        if(!String.Equals(registeredPassword,settings.OpenCodePassword,StringComparison.Ordinal))
            RunOpenCodeCommand(settings,executable,"service","set","password",settings.OpenCodePassword);
        string configuredUsername=RunOpenCodeCommand(settings,executable,"service","get","env","OPENCODE_SERVER_USERNAME").Trim();
        if(configuredUsername!="opencode")RunOpenCodeCommand(settings,executable,"service","set","env","OPENCODE_SERVER_USERNAME","opencode");
        settings.OpenCodeUsername="opencode";
    }
    /// <summary>The slow part: `service start` waits until the engine answers; run it off-thread.</summary>
    static Uri StartPreparedLocalV2Service(DesktopSettings settings,string executable)
    {
        string output=RunOpenCodeCommand(settings,executable,"service","start");
        Uri actual=null;
        foreach(string line in (output??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string candidate=line.Trim();
            Uri parsed;
            if(Uri.TryCreate(candidate,UriKind.Absolute,out parsed)&&parsed.Scheme==Uri.UriSchemeHttp&&parsed.IsLoopback){actual=new Uri(parsed.GetLeftPart(UriPartial.Authority));break;}
        }
        if(actual==null)throw new InvalidOperationException("OpenCode V2 service started, but its actual HTTP URL was not returned by `opencode service start`: "+output);
        settings.OpenCodeUrl=actual.ToString().TrimEnd('/');
        SettingsStore.Save(settings);
        return actual;
    }
    /// <summary>Waits until the prepared service answers with the configured password.</summary>
    public static void VerifyRunningV2Service(DesktopSettings settings,Uri actual,bool botStarted)
    {
        try
        {
            var request=(HttpWebRequest)WebRequest.Create(new Uri(actual,"api/info"));request.Timeout=10000;request.Proxy=null;
            request.Headers[HttpRequestHeader.Authorization]="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:"+settings.OpenCodePassword));
            using(var response=(HttpWebResponse)request.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream()))
            {
                string body=reader.ReadToEnd();
                if((int)response.StatusCode>=300||!Regex.IsMatch(body,@"""pid""\s*:\s*\d+")||!Regex.IsMatch(body,@"""version""\s*:\s*""2\."))
                    throw new InvalidOperationException("The V2 service responded, but its authenticated API/version check failed.");
            }
        }
        catch(WebException ex)
        {
            HttpWebResponse authResponse=ex.Response as HttpWebResponse;
            if(authResponse!=null&&authResponse.StatusCode==HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("The V2 service rejected the password configured by its service manager."+(botStarted?" The started Bot was stopped again.":" The Bot was not started."),ex);
            throw new InvalidOperationException("Could not verify the authenticated V2 service at "+actual+": "+ex.Message,ex);
        }
    }
    public static Uri StartLocalV2Service(DesktopSettings settings)
    {
        Uri actual=PrepareLocalV2Service(settings);
        VerifyRunningV2Service(settings,actual,false);
        return actual;
    }
    public static string StopLocalV2Service(DesktopSettings settings)
    {
        string executable=ResolveOpenCodeExecutable(settings);
        if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable))return "OpenCode V2 CLI is not installed.";
        return RunOpenCodeCommand(settings,executable,"service","stop");
    }
    public static string GetLocalV2ServiceStatus(DesktopSettings settings)
    {
        string executable=ResolveOpenCodeExecutable(settings);
        if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable))return "stopped";
        string status=RunOpenCodeCommand(settings,executable,"service","status").Trim();
        return String.IsNullOrWhiteSpace(status)?"stopped":status;
    }
    public static void ConfigureSelectedV2Installation(DesktopSettings settings,bool useExistingCredentials)
    {
        if(settings.OpenCodeServerVersion!="v2")return;
        string executable=ResolveOpenCodeExecutable(settings);
        if(ReadOpenCodeMajorVersion(ReadOpenCodeVersionFromExecutable(executable))!=2)throw new InvalidOperationException(settings.Locale=="en"?"Only OpenCode V2 can be applied.":"只能应用 OpenCode V2。");
        if(useExistingCredentials)
        {
            settings.OpenCodePassword=RunOpenCodeCommand(settings,executable,"service","get","password").Trim();
            string status=GetLocalV2ServiceStatus(settings);
            Uri existing;
            if(Uri.TryCreate(status,UriKind.Absolute,out existing)&&existing.IsLoopback)settings.OpenCodeUrl=existing.GetLeftPart(UriPartial.Authority);
            else
            {
                string port=RunOpenCodeCommand(settings,executable,"service","get","port").Trim();
                int configuredPort;
                if(Int32.TryParse(port,out configuredPort)&&configuredPort>0)settings.OpenCodeUrl=new UriBuilder("http","127.0.0.1",configuredPort).Uri.GetLeftPart(UriPartial.Authority);
            }
        }
        // The registration is shared; restart through the selected executable.
        RunOpenCodeCommand(settings,executable,"service","stop");
        StartLocalV2Service(settings);
    }
    public static string GetDefaultApiUrl(string version) { return DefaultApiUrl(version); }
    public static void ApplyOpenCode(DesktopSettings settings,string executable)
    {
        string previousExecutable=settings.OpenCodeExecutable,previousUrl=settings.OpenCodeUrl,previousPassword=settings.OpenCodePassword,previousUsername=settings.OpenCodeUsername;
        bool running=ReadServiceStatus().IndexOf("Service status: running",StringComparison.OrdinalIgnoreCase)>=0;
        try
        {
            if(ReadOpenCodeMajorVersion(ReadOpenCodeVersionFromExecutable(executable))!=2)throw new InvalidOperationException(settings.Locale=="en"?"Only OpenCode V2 can be applied.":"只能应用 OpenCode V2。");
            if(running)Stop(settings);
            settings.OpenCodeServerVersion="v2";settings.OpenCodeExecutable=executable;settings.OpenCodeUrl=DefaultV2ApiUrl;
            ConfigureSelectedV2Installation(settings,true);
            ConfigureOpenCodeCommand(settings);SettingsStore.Save(settings);
            if(running)Start(settings);
        }
        catch
        {
            settings.OpenCodeExecutable=previousExecutable;settings.OpenCodeUrl=previousUrl;settings.OpenCodePassword=previousPassword;settings.OpenCodeUsername=previousUsername;
            SettingsStore.Save(settings);ConfigureOpenCodeCommand(settings);
            try { if(!String.IsNullOrWhiteSpace(ResolveOpenCodeExecutable(settings)))ConfigureSelectedV2Installation(settings,true);if(running)Start(settings); } catch { }
            throw;
        }
    }
    static string FindOnPath(string executableName)
    {
        foreach(string directory in GetSearchPathEntries())
        {
            string candidate=Path.Combine(directory,executableName);
            if(File.Exists(candidate)) return candidate;
        }
        return "";
    }
    public static string ResolveNodeExecutable()
    {
        string bundled=Path.Combine(ProductPaths.Root,"runtime","node.exe");
        string key="node:"+ProductPaths.Root;
        lock(dependencyCache)
        {
            string selected;if(dependencyCache.TryGetValue(key,out selected))return selected;
            selected=bundled;
            try
            {
                Directory.CreateDirectory(ProductPaths.Home);
                string abi=RunAndReadOutput(bundled,"-p process.versions.modules",5000);
                string module=Path.Combine(ProductPaths.Root,"bot","node_modules","better-sqlite3").Replace("\\","/").Replace("'","\\'");
                foreach(string candidate in GetSearchPathEntries().Select(directory=>Path.Combine(directory,"node.exe")))
                {
                    if(String.IsNullOrEmpty(CompatibleTool(candidate,new Version(24,0))))continue;
                    try
                    {
                        string probe="if(process.arch!=='x64')process.exit(2);const D=require('"+module+"');const db=new D(':memory:');db.prepare('select 1').get();db.close();console.log(process.versions.modules)";
                        if(RunAndReadOutput(candidate,"-e \""+probe+"\"",5000)==abi){selected=candidate;break;}
                    }
                    catch { }
                }
            }
            catch { }
            dependencyCache[key]=selected;return selected;
        }
    }
    static string ResolveBunExecutable()
    {
        string key="bun:"+ProductPaths.Root+":"+ProductPaths.Home;
        lock(dependencyCache)
        {
            string selected;if(dependencyCache.TryGetValue(key,out selected)&&File.Exists(selected))return selected;
            var candidates=GetSearchPathEntries().Select(directory=>Path.Combine(directory,"bun.exe")).Concat(new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".bun","bin","bun.exe"),Path.Combine(ProductPaths.Home,"dependencies","bun","bun.exe"),Path.Combine(OfflineRoot,"bun","bun.exe")});
            selected=candidates.Select(candidate=>CompatibleTool(candidate,new Version(1,3))).FirstOrDefault(candidate=>!String.IsNullOrEmpty(candidate))??"";
            dependencyCache[key]=selected;return selected;
        }
    }
    static string RunWingetInstall(string packageId)
    {
        string winget=FindOnPath("winget.exe");
        if(String.IsNullOrWhiteSpace(winget)) throw new InvalidOperationException("winget is unavailable. Install App Installer from Microsoft Store, then retry.");
        var info=new ProcessStartInfo
        {
            FileName=winget,
            Arguments="install --id "+packageId+" --exact --scope user --accept-package-agreements --accept-source-agreements --disable-interactivity",
            WorkingDirectory=ProductPaths.Home,
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true,
        };
        using(var process=Process.Start(info))
        {
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(15*60*1000)){try{process.Kill();}catch{}throw new TimeoutException("Timed out installing "+packageId+" with winget.");}
            Task.WaitAll(stdout,stderr);
            if(process.ExitCode!=0) throw new InvalidOperationException((stdout.Result+Environment.NewLine+stderr.Result).Trim());
            return stdout.Result+Environment.NewLine+stderr.Result;
        }
    }
    public static string EnsureOpenCodeDependencies(string apiVersion,Action<string> progress=null,bool english=true)
    {
        string node=ResolveNodeExecutable();
        if(String.IsNullOrWhiteSpace(node))
        {
            RunWingetInstall("OpenJS.NodeJS.LTS");
            node=ResolveNodeExecutable();
        }
        if(String.IsNullOrWhiteSpace(node)) throw new InvalidOperationException("Node.js could not be found after installation. Restart the desktop app and try again.");
        if(!File.Exists(ProductPaths.NpmCli)) throw new FileNotFoundException("Bundled npm runtime is missing.",ProductPaths.NpmCli);

        if(apiVersion=="v2"&&String.IsNullOrWhiteSpace(ResolveBunExecutable()))
        {
            try { InstallBun(progress,english); }
            catch(Exception downloadError)
            {
                if(progress!=null) progress(english?"Bun download failed; trying a per-user WinGet install…":"Bun 下载失败，正在尝试通过 WinGet 为当前用户安装…");
                try { RunWingetInstall("Oven-sh.Bun"); }
                catch(Exception wingetError) { throw new InvalidOperationException("Unable to install Bun automatically. Direct download failed: "+downloadError.Message+" WinGet fallback failed: "+wingetError.Message,wingetError); }
            }
            if(String.IsNullOrWhiteSpace(ResolveBunExecutable())) throw new InvalidOperationException("Bun was installed but is not available yet. Restart the desktop app and retry.");
        }
        return node;
    }
    static void InstallBun(Action<string> progress,bool english)
    {
        const string releaseApi="https://api.github.com/repos/oven-sh/bun/releases/latest";
        ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072; // TLS 1.2
        if(progress!=null)progress(english?"Checking the latest official Bun release…":"正在检查 Bun 官方最新版本…");
        BunReleaseInfo release;
        var metadataRequest=(HttpWebRequest)WebRequest.Create(releaseApi);
        metadataRequest.UserAgent="opencode_telegram_bot";metadataRequest.Accept="application/vnd.github+json";metadataRequest.Timeout=30000;
        using(var response=(HttpWebResponse)metadataRequest.GetResponse())
        using(var stream=response.GetResponseStream()) release=(BunReleaseInfo)new DataContractJsonSerializer(typeof(BunReleaseInfo)).ReadObject(stream);
        BunReleaseAsset asset=release.Assets==null?null:release.Assets.FirstOrDefault(a=>a.Name=="bun-windows-x64.zip");
        if(asset==null||asset.Size<1024||asset.Size>150L*1024*1024||String.IsNullOrWhiteSpace(asset.Digest)||!asset.Digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The official Bun release did not provide a valid Windows x64 archive and SHA-256 digest.");
        Uri downloadUri;if(!Uri.TryCreate(asset.DownloadUrl,UriKind.Absolute,out downloadUri)||downloadUri.Scheme!="https"||!downloadUri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The Bun release download URL is not an approved HTTPS GitHub URL.");
        string download=Path.Combine(Path.GetTempPath(),"opencode_telegram_bun_"+Guid.NewGuid().ToString("N")+".zip");
        try
        {
            var request=(HttpWebRequest)WebRequest.Create(downloadUri);request.UserAgent="opencode_telegram_bot";request.Timeout=60000;request.ReadWriteTimeout=60000;
            using(var response=(HttpWebResponse)request.GetResponse())
            using(var input=response.GetResponseStream())
            using(var output=new FileStream(download,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                byte[] buffer=new byte[1024*1024];long total=0;int read,lastPercent=-1;
                while((read=input.Read(buffer,0,buffer.Length))>0)
                {
                    output.Write(buffer,0,read);total+=read;int percent=(int)Math.Min(100,total*100/asset.Size);
                    if(progress!=null&&percent!=lastPercent){progress((english?"Downloading official Bun for Windows x64… ":"正在下载 Bun Windows x64 官方版本… ")+percent+"% ("+total/1048576+" / "+asset.Size/1048576+" MiB)");lastPercent=percent;}
                    if(total>asset.Size)throw new InvalidDataException("The Bun download exceeded the size reported by GitHub.");
                }
                output.Flush();if(total!=asset.Size)throw new InvalidDataException("The Bun download ended early ("+total+" of "+asset.Size+" bytes).");
            }
            string expected=asset.Digest.Substring("sha256:".Length).Trim().ToLowerInvariant();string actual;
            using(var sha=SHA256.Create())using(var input=File.OpenRead(download))actual=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
            if(!String.Equals(expected,actual,StringComparison.Ordinal))throw new InvalidDataException("Bun archive SHA-256 verification failed.");
            string bunDirectory=Path.Combine(ProductPaths.Home,"dependencies","bun");Directory.CreateDirectory(bunDirectory);
            string staged=Path.Combine(bunDirectory,"bun.exe.download");
            try
            {
                using(var file=File.OpenRead(download))using(var archive=new ZipArchive(file,ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry=archive.Entries.FirstOrDefault(e=>String.Equals(e.Name,"bun.exe",StringComparison.OrdinalIgnoreCase));
                    if(entry==null)throw new InvalidDataException("The official Bun archive does not contain bun.exe.");
                    entry.ExtractToFile(staged,true);
                }
                var versionInfo=new ProcessStartInfo{FileName=staged,Arguments="--version",WorkingDirectory=bunDirectory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                using(var process=Process.Start(versionInfo)){string version=process.StandardOutput.ReadToEnd();string error=process.StandardError.ReadToEnd();if(!process.WaitForExit(30000)||process.ExitCode!=0)throw new InvalidOperationException("Downloaded Bun failed its version check: "+(error+Environment.NewLine+version).Trim());}
                string installed=Path.Combine(bunDirectory,"bun.exe");if(File.Exists(installed))File.Delete(installed);File.Move(staged,installed);
            }
            finally { if(File.Exists(staged))File.Delete(staged); }
            if(progress!=null)progress(english?"Bun installed for this Windows user.":"已为当前 Windows 用户安装 Bun。");
        }
        finally { try{if(File.Exists(download))File.Delete(download);}catch{} }
    }
    static ProcessStartInfo Build(string cliArgs, DesktopSettings s, bool capture)
    {
        var psi = new ProcessStartInfo { FileName = ProductPaths.Node, Arguments = Quote(ProductPaths.BotCli) + " " + cliArgs, WorkingDirectory = ProductPaths.Home, UseShellExecute = false, CreateNoWindow = capture, RedirectStandardOutput = capture, RedirectStandardError = capture };
        Directory.CreateDirectory(ProductPaths.Home);
        var env = psi.EnvironmentVariables;
        env[ProductPaths.EnvRoot] = ProductPaths.Root;
        env[ProductPaths.EnvHome] = ProductPaths.Home;
        env["OPENCODE_TELEGRAM_RUNTIME_MODE"] = "installed";
        env["OPENCODE_TELEGRAM_DESKTOP_SYNC"] = "1";
        env["TELEGRAM_BOT_TOKEN"] = s.BotToken ?? "";
        env["TELEGRAM_ALLOWED_USER_ID"] = s.AllowedUserId ?? "";
        env["BOT_LOCALE"] = s.Locale ?? "zh";
        env["OPENCODE_SERVER_VERSION"] = "v2";
        env["OPENCODE_API_URL"] = ResolveOpenCodeUrl(s);
        env["OPENCODE_SERVER_USERNAME"] = s.OpenCodeUsername ?? "opencode";
        env["OPENCODE_SERVER_PASSWORD"] = s.OpenCodePassword ?? "";
        if(s.OpenCodeServerVersion=="v2") env["OPENCODE_PASSWORD"] = s.OpenCodePassword ?? "";
        env["OPENCODE_MODEL_PROVIDER"] = s.ModelProvider ?? "opencode";
        env["OPENCODE_MODEL_ID"] = s.ModelId ?? "big-pickle";
        env["OPENCODE_TELEGRAM_EOT10_ENABLED"] = s.Eot10Enabled && !String.IsNullOrEmpty(s.Eot10Password) ? "1" : "0";
        env["OPENCODE_TELEGRAM_EOT10_PASSWORD"] = s.Eot10Enabled ? (s.Eot10Password ?? "") : "";
        env["OPENCODE_AUTO_RESTART_ENABLED"] = "true";
        // The desktop starts the engine itself; the Bot must not spawn a competing instance.
        env["OPENCODE_MANAGED_BY_DESKTOP"] = "1";
        string openCodeExecutable=ResolveOpenCodeExecutable(s);
        if (File.Exists(openCodeExecutable)) env["OPENCODE_EXECUTABLE_PATH"] = openCodeExecutable;
        env["PATH"] = BuildOpenCodePath(s);
        return psi;
    }
    static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    public static string Execute(string args, DesktopSettings settings, int timeoutSeconds = 30)
    {
        using (var process = Process.Start(Build(args, settings, true)))
        {
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutSeconds * 1000)) { try { process.Kill(); } catch { } throw new TimeoutException("Bot command timed out."); }
            string result = (stdout + Environment.NewLine + stderr).Trim();
            if (process.ExitCode != 0) throw new InvalidOperationException(String.IsNullOrWhiteSpace(result) ? "Bot command failed." : result);
            return result;
        }
    }
    public static string Start(DesktopSettings settings,Action<string> progress=null)
    {
        EnsureLocalV2Password(settings);
        if(IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings))&&File.Exists(ResolveOpenCodeExecutable(settings))) EnsureOpenCodeDependencies("v2",progress,settings.Locale=="en");
        EnsureOpenCodeInstalled(settings);
        var stopped = StopCompetingBotProcesses();
        bool localV2=settings.OpenCodeServerVersion=="v2"&&IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings));
        Uri serviceUrl=null;
        var stoppedOpenCode = new List<int>();
        Task<Uri> serviceStartTask=null;
        if(localV2&&TryUseRunningV2Service(settings,out serviceUrl))
        {
            // The service manager's own state says a service is alive on the configured port and
            // it answers with the configured password: restarting it would only cost seconds and
            // throw away its warm model catalog. Applying an engine restarts it explicitly.
            if(progress!=null)progress((settings.Locale=="en"?"Reusing the running OpenCode V2 service at ":"复用正在运行的 OpenCode V2 服务：")+serviceUrl);
        }
        else
        {
            stoppedOpenCode = StopOpenCodeProcesses();
            if(localV2)
            {
                // Registration is quick; `service start` keeps booting the engine in the
                // background while the Bot performs its own Telegram startup.
                string executable=RequireV2Executable(settings);
                ConfigureLocalV2ServiceRegistration(settings,executable);
                serviceStartTask=Task.Run(new Func<Uri>(()=>StartPreparedLocalV2Service(settings,executable)));
                if(progress!=null)progress((settings.Locale=="en"?"Starting OpenCode V2 and the Bot together…":"正在同时启动 OpenCode V2 与 Bot…"));
            }
        }
        string result;
        try
        {
            result = Execute("start --daemon --mode installed", settings, 60);
            if(serviceStartTask!=null)
            {
                serviceUrl=serviceStartTask.GetAwaiter().GetResult();
                VerifyRunningV2Service(settings,serviceUrl,true);
                if(progress!=null)progress((settings.Locale=="en"?"Authenticated OpenCode V2 service ready at ":"OpenCode V2 服务已验证并就绪：")+serviceUrl);
            }
        }
        catch
        {
            if(serviceStartTask!=null)
            {
                try{Execute("stop",settings,30);}catch{}
            }
            throw;
        }
        if(stopped.Count>0) result="Stopped conflicting Bot process(es): "+String.Join(", ",stopped)+Environment.NewLine+result;
        if(stoppedOpenCode.Count>0) result="Stopped OpenCode process(es): "+String.Join(", ",stoppedOpenCode)+Environment.NewLine+result;
        return result;
    }
    public static List<int> StopOpenCodeProcesses()
    {
        var stopped=new List<int>();
        var processParents=new Dictionary<int,int>();
        var openCodeProcesses=new HashSet<int>();
        using(var search=new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process"))
        using(var results=search.Get())
        {
            foreach(ManagementObject item in results)
            {
                try
                {
                    int pid=Convert.ToInt32(item["ProcessId"]);
                    int parentPid=Convert.ToInt32(item["ParentProcessId"]);
                    string name=item["Name"] as string??"";
                    string commandLine=item["CommandLine"] as string??"";
                    processParents[pid]=parentPid;
                    bool isOpenCode=String.Equals(Path.GetFileNameWithoutExtension(name),"opencode",StringComparison.OrdinalIgnoreCase)
                        ||Regex.IsMatch(commandLine,@"(?i)(?:^|[\s""'])[^\s""']*[\\/]opencode(?:\.exe|\.cmd)?(?=$|[\s""'])")
                        ||commandLine.IndexOf("opencode-ai",StringComparison.OrdinalIgnoreCase)>=0
                        ||commandLine.IndexOf("@opencode/cli",StringComparison.OrdinalIgnoreCase)>=0
                        ||commandLine.IndexOf("@opencode\\cli",StringComparison.OrdinalIgnoreCase)>=0;
                    if(isOpenCode) openCodeProcesses.Add(pid);
                }
                catch(FormatException) { }
                catch(InvalidCastException) { }
            }
        }

        bool added;
        do
        {
            added=false;
            foreach(var process in processParents)
                if(openCodeProcesses.Contains(process.Value)&&openCodeProcesses.Add(process.Key)) added=true;
        } while(added);

        foreach(int pid in openCodeProcesses.OrderByDescending(value=>value))
        {
            try
            {
                using(var process=Process.GetProcessById(pid))
                {
                    process.Kill();
                    if(!process.WaitForExit(3000)) throw new InvalidOperationException("Timed out stopping OpenCode process PID="+pid+".");
                    stopped.Add(pid);
                }
            }
            catch(ArgumentException) { }
            catch(System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("Unable to stop OpenCode process PID="+pid+": "+ex.Message,ex); }
        }
        return stopped;
    }
    static List<int> StopOpenCodeProcessesUnder(string engineHome)
    {
        string root=Path.GetFullPath(engineHome).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var stopped=new List<int>();
        var parentByPid=new Dictionary<int,int>();
        var targets=new HashSet<int>();
        using(var search=new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
        using(var results=search.Get())
        {
            foreach(ManagementObject item in results)
            {
                try
                {
                    int pid=Convert.ToInt32(item["ProcessId"]);
                    int parentPid=Convert.ToInt32(item["ParentProcessId"]);
                    parentByPid[pid]=parentPid;
                    string executable=item["ExecutablePath"] as string??"";
                    string commandLine=item["CommandLine"] as string??"";
                    bool underEngine=(!String.IsNullOrWhiteSpace(executable)&&Path.GetFullPath(executable).StartsWith(root,StringComparison.OrdinalIgnoreCase))
                        ||commandLine.IndexOf(root,StringComparison.OrdinalIgnoreCase)>=0;
                    if(underEngine) targets.Add(pid);
                }
                catch(FormatException) { }
                catch(InvalidCastException) { }
            }
        }

        bool added;
        do
        {
            added=false;
            foreach(var process in parentByPid)
                if(targets.Contains(process.Value)&&targets.Add(process.Key)) added=true;
        } while(added);

        foreach(int pid in targets.OrderByDescending(value=>value))
        {
            try
            {
                using(var process=Process.GetProcessById(pid))
                {
                    process.Kill();
                    if(!process.WaitForExit(3000)) throw new InvalidOperationException("Timed out stopping OpenCode process PID="+pid+".");
                    stopped.Add(pid);
                }
            }
            catch(ArgumentException) { }
            catch(System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("Unable to stop OpenCode process PID="+pid+": "+ex.Message,ex); }
        }
        return stopped;
    }
    public static List<int> StopCompetingBotProcesses()
    {
        var stopped = new List<int>();
        using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='node.exe'"))
        using (var results = search.Get())
        {
            foreach (ManagementObject item in results)
            {
                string command = item["CommandLine"] as string;
                if (String.IsNullOrWhiteSpace(command)) continue;
                string normalized = command.Replace('/', '\\');
                int scriptIndex = normalized.IndexOf(@"dist\cli.js", StringComparison.OrdinalIgnoreCase);
                if (scriptIndex < 0) continue;
                string arguments = normalized.Substring(scriptIndex + @"dist\cli.js".Length).TrimStart(' ', '"', '\'');
                if (!arguments.StartsWith("start", StringComparison.OrdinalIgnoreCase) || (arguments.Length > 5 && !Char.IsWhiteSpace(arguments[5]))) continue;
                int pid = Convert.ToInt32(item["ProcessId"]);
                if (pid == Process.GetCurrentProcess().Id) continue;
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        process.Kill();
                        if (!process.WaitForExit(10000)) throw new InvalidOperationException("Timed out stopping conflicting Bot process PID=" + pid + ".");
                        stopped.Add(pid);
                    }
                }
                catch (ArgumentException) { }
            }
        }
        return stopped;
    }
    public static string Status(DesktopSettings settings) { return Execute("status", settings, 20); }
    public static string Stop(DesktopSettings settings) { return Execute("stop", settings, 30); }
    static string ServiceStatePath { get { return Path.Combine(ProductPaths.Home,"run","bot-service.json"); } }
    static string OpenCodeServiceStatePath
    {
        get
        {
            string xdg=Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if(!String.IsNullOrWhiteSpace(xdg))return Path.Combine(xdg,"opencode","service.json");
            string home=Environment.GetEnvironmentVariable("HOME");
            if(String.IsNullOrWhiteSpace(home))home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home,".local","state","opencode","service.json");
        }
    }
    /// <summary>
    /// The same text `status` prints, read from the daemon state file and process table.
    /// Cheap enough for the 2-second dashboard poll: it never starts a Node process.
    /// </summary>
    public static string ReadServiceStatus()
    {
        const string stopped="Service status: stopped";
        BotServiceState state=null;
        try
        {
            using(var stream=new FileStream(ServiceStatePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                state=(BotServiceState)new DataContractJsonSerializer(typeof(BotServiceState)).ReadObject(stream);
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        catch(SerializationException) { }
        if(state==null||state.Pid<=0)return stopped;

        DateTime startedAt;
        bool hasStartedAt=DateTime.TryParse(state.StartedAt,CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out startedAt);
        try
        {
            using(var process=Process.GetProcessById(state.Pid))
            {
                if(process.HasExited)return stopped;
                // A reused PID is not the daemon that wrote this file.
                if(hasStartedAt&&process.StartTime.ToUniversalTime()>startedAt)return stopped;
            }
        }
        catch(ArgumentException) { return stopped; }
        catch(InvalidOperationException) { return stopped; }
        catch(System.ComponentModel.Win32Exception) { return stopped; }

        long uptimeSec=hasStartedAt?Math.Max(0,(long)(DateTime.UtcNow-startedAt).TotalSeconds):0;
        return String.Join("\n",new[]{
            "Service status: running",
            "PID: "+state.Pid.ToString(CultureInfo.InvariantCulture),
            "Started at: "+(state.StartedAt??""),
            "Uptime: "+uptimeSec.ToString(CultureInfo.InvariantCulture)+" sec",
            "Log file: "+(state.LogFilePath??""),
            "App home: "+ProductPaths.Home
        });
    }
    /// <summary>
    /// Reads the V2 service manager state and decides whether that service is the one this
    /// configuration needs: alive, loopback and on the configured port.
    /// </summary>
    static bool TryReadRunningV2Service(DesktopSettings settings,out Uri serviceUrl)
    {
        serviceUrl=null;
        if(settings.OpenCodeServerVersion!="v2"||!IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings)))return false;
        OpenCodeServiceState state=null;
        try
        {
            using(var stream=new FileStream(OpenCodeServiceStatePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                state=(OpenCodeServiceState)new DataContractJsonSerializer(typeof(OpenCodeServiceState)).ReadObject(stream);
        }
        catch(IOException) { return false; }
        catch(UnauthorizedAccessException) { return false; }
        catch(SerializationException) { return false; }
        if(state==null||state.Pid<=0||String.IsNullOrWhiteSpace(state.Url))return false;

        try { using(var process=Process.GetProcessById(state.Pid)) { if(process.HasExited)return false; } }
        catch(ArgumentException) { return false; }
        catch(InvalidOperationException) { return false; }
        catch(System.ComponentModel.Win32Exception) { return false; }

        Uri actual;
        Uri requested;
        if(!Uri.TryCreate(state.Url,UriKind.Absolute,out actual)||!actual.IsLoopback)return false;
        if(!Uri.TryCreate(ResolveOpenCodeUrl(settings),UriKind.Absolute,out requested)||requested.Port!=actual.Port)return false;
        serviceUrl=actual;
        return true;
    }
    /// <summary>
    /// True when the V2 service recorded by its own service manager is alive on the configured
    /// port and accepts the configured password. Reusing it avoids a full engine restart.
    /// </summary>
    public static bool TryUseRunningV2Service(DesktopSettings settings,out Uri serviceUrl)
    {
        if(!TryReadRunningV2Service(settings,out serviceUrl))return false;
        try
        {
            var request=(HttpWebRequest)WebRequest.Create(new Uri(serviceUrl,"api/info"));
            request.Timeout=2000;request.ReadWriteTimeout=2000;request.Proxy=null;
            string credentials=(settings.OpenCodeUsername??"opencode")+":"+(settings.OpenCodePassword??"");
            request.Headers[HttpRequestHeader.Authorization]="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            using(var response=(HttpWebResponse)request.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream()))
            {
                string body=reader.ReadToEnd();
                if((int)response.StatusCode>=300||!Regex.IsMatch(body,@"""version""\s*:\s*""2\."))return false;
            }
        }
        catch(Exception)
        {
            serviceUrl=null;
            return false;
        }
        return true;
    }
    public static void ConfigureOpenCodeCommand(DesktopSettings settings)
    {
        string executable=ResolveOpenCodeExecutable(settings);
        string bin=Path.Combine(ProductPaths.Root,"bin"); Directory.CreateDirectory(bin);
        string shim=Path.Combine(bin,"opencode.cmd");
        if(String.IsNullOrWhiteSpace(executable)) { if(File.Exists(shim))File.Delete(shim); return; }
        if(executable.IndexOfAny(new[]{'\r','\n','"'})>=0)throw new InvalidDataException("Invalid OpenCode executable path.");
        string path=BuildOpenCodePath(settings,executable).Replace("%","%%");
        string target=executable.Replace("%","%%");
        string invocation=Path.GetExtension(executable).Equals(".cmd",StringComparison.OrdinalIgnoreCase)?"call ":"";
        string content="@echo off\r\nsetlocal\r\nset \"PATH="+path+"\"\r\n"+invocation+"\""+target+"\" %*\r\nexit /b %errorlevel%\r\n";
        string temp=shim+".tmp"; File.WriteAllText(temp,content,Encoding.Default);
        if(File.Exists(shim))File.Replace(temp,shim,null);else File.Move(temp,shim);
        string old=Environment.GetEnvironmentVariable("PATH")??"";
        var entries=old.Split(Path.PathSeparator).Where(x=>!String.Equals(x.TrimEnd('\\','/'),bin.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH",bin+Path.PathSeparator+String.Join(Path.PathSeparator.ToString(),entries));
    }
    [DataContract] sealed class NpmRelease { [DataMember(Name="version")] public string Version { get; set; } }
    public static string UpdateOpenCode(DesktopSettings settings,Action<string> progress)
    {
        string applied=ResolveOpenCodeExecutable(settings);
        if(String.IsNullOrWhiteSpace(applied))throw new InvalidOperationException(settings.Locale=="en"?"Apply an OpenCode V2 installation first.":"请先应用一个 OpenCode V2 版本。");
        Version current=ParseOpenCodeVersion(ReadOpenCodeVersionFromExecutable(applied));
        ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
        var request=(HttpWebRequest)WebRequest.Create("https://registry.npmjs.org/@opencode%2fcli/latest"); request.Timeout=30000;
        NpmRelease release;
        using(var response=request.GetResponse())using(var stream=response.GetResponseStream())release=(NpmRelease)new DataContractJsonSerializer(typeof(NpmRelease)).ReadObject(stream);
        if(release==null||!System.Text.RegularExpressions.Regex.IsMatch(release.Version??"","^2\\.[0-9]+\\.[0-9]+$"))throw new InvalidDataException(settings.Locale=="en"?"The registry did not return a stable V2 release.":"官方仓库未返回稳定的 V2 版本。");
        Version latest=new Version(release.Version);
        if(latest<=current)return null;
        string engineHome=Path.Combine(ProductPaths.Home,"opencode-runtime","v2","versions",release.Version+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(engineHome);
        string node=EnsureOpenCodeDependencies("v2",progress,settings.Locale=="en");
        if(progress!=null)progress(settings.Locale=="en"?"Downloading OpenCode "+release.Version+" in a visible terminal; npm progress is shown there.":"正在可见终端下载 OpenCode "+release.Version+"，npm 进度将在该窗口显示。");
        var info=new ProcessStartInfo { FileName=Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe", Arguments="/d /c \"\""+node+"\" \""+ProductPaths.NpmCli+"\" install --prefix \""+engineHome+"\" --registry=https://registry.npmjs.org --progress=true --no-audit --no-fund \"@opencode/cli@"+release.Version+"\"\"", WorkingDirectory=engineHome, UseShellExecute=true, WindowStyle=ProcessWindowStyle.Normal };
        using(var process=Process.Start(info)){if(!process.WaitForExit(600000)){try{process.Kill();}catch{}throw new TimeoutException(settings.Locale=="en"?"OpenCode update download timed out.":"OpenCode 更新下载超时。");}if(process.ExitCode!=0)throw new IOException(settings.Locale=="en"?"OpenCode download failed; the applied version was not changed.":"OpenCode 下载失败，当前应用版本未改变。");}
        string executable=Path.Combine(engineHome,"node_modules",".bin","opencode.cmd");
        if(!File.Exists(executable)||ParseOpenCodeVersion(ReadOpenCodeVersionFromExecutable(executable))!=latest)throw new InvalidDataException(settings.Locale=="en"?"Downloaded OpenCode failed version verification.":"下载的 OpenCode 未通过版本验证。");
        return executable;
    }
    public static string InstallOpenCode(DesktopSettings settings, string apiVersion,Action<string> progress=null)
    {
        if(apiVersion!="v2") throw new ArgumentException("Only OpenCode V2 is supported.","apiVersion");
        Directory.CreateDirectory(ProductPaths.Home);
        string bundled=Path.Combine(OfflineRoot,"opencode",apiVersion,"opencode.exe");
        if(File.Exists(bundled))
        {
            EnsureOpenCodeDependencies(apiVersion,progress,settings.Locale=="en");
            OpenCodeInstallation existing=null;
            try { existing=ScanOpenCodeInstallations(settings).FirstOrDefault(item=>item.ApiVersion==apiVersion); } catch { }
            string chosen=existing==null?bundled:existing.Executable;
            if(ReadOpenCodeMajorVersion(ReadOpenCodeVersionFromExecutable(chosen))!=(apiVersion=="v2"?2:1))throw new InvalidOperationException("Bundled OpenCode API version mismatch.");
            if(progress!=null)progress((settings.Locale=="en"?"Using compatible offline/local engine: ":"使用兼容的本机或包内引擎：")+chosen);
            return chosen;
        }
        string node=EnsureOpenCodeDependencies(apiVersion,progress,settings.Locale=="en");
        string engineHome = Path.Combine(ProductPaths.Home, "opencode-runtime", apiVersion);
        string package = apiVersion=="v2"?"@opencode/cli@^2":"opencode-ai@^1";
        Directory.CreateDirectory(engineHome);
        var psi = new ProcessStartInfo { FileName=node, Arguments="\""+ProductPaths.NpmCli+"\" install --prefix \""+engineHome+"\" --progress=true \""+package+"\"", WorkingDirectory=engineHome, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        using(var process=Process.Start(psi))
        {
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync(); process.WaitForExit();
            if(process.ExitCode!=0) throw new InvalidOperationException((stdout.Result+Environment.NewLine+stderr.Result).Trim());
        }
        string executable=Path.Combine(engineHome,"node_modules",".bin","opencode.cmd");
        if(!File.Exists(executable)) throw new FileNotFoundException("The installed OpenCode CLI shim was not created.",executable);
        string versionOutput=ReadOpenCodeVersion(settings,executable);
        int expectedMajor=apiVersion=="v2"?2:1;
        if(ReadOpenCodeMajorVersion(versionOutput)!=expectedMajor) throw new InvalidOperationException("Installed CLI version does not match API "+apiVersion+": "+versionOutput);
        return executable;
    }
    public static void UninstallOpenCode(string apiVersion)
    {
        if(apiVersion!="v2") throw new ArgumentException("Only OpenCode V2 is supported.","apiVersion");
        string engineHome=Path.Combine(ProductPaths.Home,"opencode-runtime",apiVersion);
        if(!Directory.Exists(engineHome)) return;
        StopOpenCodeProcessesUnder(engineHome);
        Directory.Delete(engineHome,true);
        if(Directory.Exists(engineHome)) throw new IOException("OpenCode API "+apiVersion+" install folder still exists after uninstall.");
    }
    public static void UninstallOpenCodeInstallation(OpenCodeInstallation installation)
    {
        string executable=Path.GetFullPath(installation.Executable);
        string managed=Path.GetFullPath(Path.Combine(ProductPaths.Home,"opencode-runtime","v2"));
        string bundled=Path.GetFullPath(Path.Combine(OfflineRoot,"opencode","v2"));
        string folder;
        if(executable.StartsWith(bundled+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))folder=bundled;
        else if(executable.StartsWith(managed+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
        {
            string versions=Path.Combine(managed,"versions")+Path.DirectorySeparatorChar;
            folder=executable.StartsWith(versions,StringComparison.OrdinalIgnoreCase)?Path.Combine(versions,executable.Substring(versions.Length).Split(Path.DirectorySeparatorChar)[0]):Path.Combine(managed,"node_modules");
        }
        else throw new InvalidOperationException("External installations must be removed with their original installer or package manager.");
        StopOpenCodeProcessesUnder(folder); if(Directory.Exists(folder))Directory.Delete(folder,true);
        lock(dependencyCache)dependencyCache.Clear();
    }
    public static void RemoveLegacyOpenCodePayload(string root)
    {
        foreach(string folder in new[]{Path.Combine(root,"dependencies","opencode","v1"),Path.Combine(root,"bot","node_modules","@opencode-ai","sdk")})
        {
            if(!Directory.Exists(folder))continue;
            StopOpenCodeProcessesUnder(folder);Directory.Delete(folder,true);
        }
    }
}
