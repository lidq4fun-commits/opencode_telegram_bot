using System;
using System.Collections.Generic;
using System.Diagnostics;
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

[DataContract]
sealed class DesktopSettings
{
    public DesktopSettings()
    {
        BotToken = ""; AllowedUserId = ""; Locale = "zh"; Eot10Password = "";
        OpenCodeUrl = "http://127.0.0.1:4096"; OpenCodeServerVersion = "v1"; OpenCodeUsername = "opencode";
        OpenCodePassword = ""; ModelProvider = "opencode"; ModelId = "big-pickle";
        OpenCodeExecutable = "";
    }
    [DataMember] public string BotToken { get; set; }
    [DataMember] public string AllowedUserId { get; set; }
    [DataMember] public string Locale { get; set; }
    [DataMember] public bool Eot10Enabled { get; set; }
    [DataMember] public string Eot10Password { get; set; }
    [DataMember] public string OpenCodeUrl { get; set; }
    [DataMember] public string OpenCodeServerVersion { get; set; }
    [DataMember] public string OpenCodeUsername { get; set; }
    [DataMember] public string OpenCodePassword { get; set; }
    [DataMember] public string ModelProvider { get; set; }
    [DataMember] public string ModelId { get; set; }
    [DataMember] public string OpenCodeExecutable { get; set; }
    [DataMember] public bool AutoStart { get; set; }
    [DataMember] public bool AutoStartApp { get; set; }
    [DataMember] public string WebFollowMode { get; set; }
    public string ResolvedWebFollowMode { get { return WebFollowMode=="off"||WebFollowMode=="force"?WebFollowMode:"prompt"; } }
}

[DataContract]
sealed class TelegramWebContext
{
    [DataMember(Name="currentProject")] public TelegramWebProject Project { get; set; }
    [DataMember(Name="currentSession")] public TelegramWebSession Session { get; set; }

    public bool HasSession
    {
        get
        {
            if(Project==null||Session==null||String.IsNullOrWhiteSpace(Project.Directory)||String.IsNullOrWhiteSpace(Session.Id)||String.IsNullOrWhiteSpace(Session.Directory))return false;
            string project=Project.Directory.Replace('\\','/').TrimEnd('/'),session=Session.Directory.Replace('\\','/').TrimEnd('/');
            var comparison=Regex.IsMatch(project,@"^[A-Za-z]:/")?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            return String.Equals(project,session,comparison);
        }
    }
    public Uri WebAddress(Uri server,string version)
    {
        if(!HasSession)return server;
        string authority=server.GetLeftPart(UriPartial.Authority);
        string key=version=="v2"?authority:Session.Directory;
        string encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+','-').Replace('/','_');
        string path=version=="v2"?"/server/"+encoded:"/"+encoded;
        return new Uri(authority+path+"/session/"+Uri.EscapeDataString(Session.Id));
    }
    public static bool TryLoad(string home,out TelegramWebContext context)
    {
        context=null;
        bool found=false;
        foreach(string path in new[]{Path.Combine(home,"settings.json"),Path.Combine(home,"settings.json.bak")})
        {
            if(!File.Exists(path))continue;
            found=true;
            try
            {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                    context=(TelegramWebContext)new DataContractJsonSerializer(typeof(TelegramWebContext)).ReadObject(stream);
                if(context!=null)return true;
            }
            catch(IOException){}catch(UnauthorizedAccessException){}catch(SerializationException){}
        }
        if(found)return false;
        context=new TelegramWebContext();
        return true;
    }
}
[DataContract]
sealed class TelegramWebProject
{
    [DataMember(Name="worktree")] public string Directory { get; set; }
    [DataMember(Name="name")] public string Name { get; set; }
}
[DataContract]
sealed class TelegramWebSession
{
    [DataMember(Name="id")] public string Id { get; set; }
    [DataMember(Name="directory")] public string Directory { get; set; }
    [DataMember(Name="title")] public string Title { get; set; }
}

[DataContract]
sealed class BrowserPairingCode
{
    [DataMember(Name="code")] public string Code { get; set; }
}

// A one-use loopback redirect transfers OpenCode's own HttpOnly cookie to the
// browser. Cookies are host-scoped, so the target service port remains unchanged.
sealed class BrowserLoginRedirect : IDisposable
{
    readonly System.Net.Sockets.TcpListener listener;
    readonly Uri target;
    readonly string[] cookies;
    readonly string path;
    public Uri Address { get; private set; }

    public BrowserLoginRedirect(Uri target,string[] cookies)
    {
        if(!target.IsLoopback||(target.Scheme!="http"&&target.Scheme!="https"))throw new ArgumentException("A local HTTP service is required.");
        if(cookies==null||cookies.Length==0||cookies.Any(value=>String.IsNullOrWhiteSpace(value)||value.Any(c=>c<' '||c==127)))throw new ArgumentException("Invalid browser session cookie.");
        this.target=target;this.cookies=cookies;
        path="/"+Guid.NewGuid().ToString("N");
        IPAddress bindAddress;
        if(!IPAddress.TryParse(target.DnsSafeHost,out bindAddress))bindAddress=IPAddress.Loopback;
        listener=new System.Net.Sockets.TcpListener(bindAddress,0);
        listener.Start();
        Address=new UriBuilder("http",target.DnsSafeHost,((IPEndPoint)listener.LocalEndpoint).Port,path).Uri;
    }

    public async Task CompleteAsync()
    {
        var timer=Stopwatch.StartNew();
        try
        {
            while(timer.ElapsedMilliseconds<60000)
            {
                var accept=listener.AcceptTcpClientAsync();
                if(await Task.WhenAny(accept,Task.Delay(Math.Max(1,60000-(int)timer.ElapsedMilliseconds)))!=accept)throw new TimeoutException("The browser did not complete the local login redirect.");
                using(var client=await accept)
                using(var stream=client.GetStream())
                {
                    string request;
                    try{request=await ReadRequest(stream);}catch(IOException){continue;}
                    var first=request.Split(new[]{"\r\n"},StringSplitOptions.None)[0].Split(' ');
                    var host=Regex.Match(request,@"(?im)^Host:\s*([^\r\n]+)");
                    bool valid=first.Length==3&&first[0]=="GET"&&first[1]==path&&first[2].StartsWith("HTTP/1.",StringComparison.Ordinal)&&host.Success&&String.Equals(host.Groups[1].Value.Trim(),Address.Authority,StringComparison.OrdinalIgnoreCase);
                    string response=valid
                        ? "HTTP/1.1 303 See Other\r\nLocation: "+target.AbsoluteUri+"\r\n"+String.Join("",cookies.Select(cookie=>"Set-Cookie: "+cookie+"\r\n"))
                        : "HTTP/1.1 404 Not Found\r\n";
                    response+="Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    byte[] bytes=Encoding.ASCII.GetBytes(response);
                    try{await stream.WriteAsync(bytes,0,bytes.Length);}catch(IOException){continue;}
                    if(valid)return;
                }
            }
            throw new TimeoutException("The browser did not complete the local login redirect.");
        }
        finally{Dispose();}
    }

    static async Task<string> ReadRequest(System.Net.Sockets.NetworkStream stream)
    {
        var buffer=new byte[1024];var timer=Stopwatch.StartNew();
        using(var data=new MemoryStream())
        {
            while(data.Length<8192&&timer.ElapsedMilliseconds<5000)
            {
                var read=stream.ReadAsync(buffer,0,Math.Min(buffer.Length,8192-(int)data.Length));
                if(await Task.WhenAny(read,Task.Delay(Math.Max(1,5000-(int)timer.ElapsedMilliseconds)))!=read)break;
                int count=await read;if(count==0)break;
                data.Write(buffer,0,count);
                string text=Encoding.ASCII.GetString(data.ToArray());
                if(text.Contains("\r\n\r\n"))return text;
            }
            return "";
        }
    }
    public void Dispose(){listener.Stop();}
}

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

static class ProductPaths
{
    public const string AppName = "opencode_telegram_bot";
    public const string EnvRoot = "OPENCODE_TELEGRAM_ROOT";
    public const string EnvHome = "OPENCODE_TELEGRAM_HOME";
    public static string Root { get { return Environment.GetEnvironmentVariable(EnvRoot) ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); } }
    public static string Home { get { return Environment.GetEnvironmentVariable(EnvHome) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenCodeTelegram"); } }
    public static string SettingsFile { get { return Path.Combine(Home, "desktop-settings.dpapi"); } }
    public static string BotCli { get { return Path.Combine(Root, "bot", "dist", "cli.js"); } }
    public static string Node { get { return Path.Combine(Root, "runtime", "node.exe"); } }
    public static string NpmCli { get { return Path.Combine(Root, "runtime", "node_modules", "npm", "bin", "npm-cli.js"); } }
    public static string BotModulesBin { get { return Path.Combine(Root, "bot", "node_modules", ".bin"); } }
}

static class SettingsStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenCodeTelegramDesktopSettings-v1");
    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(ProductPaths.SettingsFile)) return new DesktopSettings();
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(ProductPaths.SettingsFile), Entropy, DataProtectionScope.CurrentUser);
            try { using (var stream = new MemoryStream(clear)) return (DesktopSettings)new DataContractJsonSerializer(typeof(DesktopSettings)).ReadObject(stream); }
            finally { Array.Clear(clear, 0, clear.Length); }
        }
        catch { return new DesktopSettings(); }
    }
    public static void Save(DesktopSettings settings)
    {
        Directory.CreateDirectory(ProductPaths.Home);
        byte[] clear;
        using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(DesktopSettings)).WriteObject(stream, settings); clear = stream.ToArray(); }
        byte[] protectedBytes = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        string temp = ProductPaths.SettingsFile + ".tmp";
        try
        {
            File.WriteAllBytes(temp, protectedBytes);
            if (File.Exists(ProductPaths.SettingsFile)) File.Replace(temp, ProductPaths.SettingsFile, null);
            else File.Move(temp, ProductPaths.SettingsFile);
        }
        finally { Array.Clear(clear, 0, clear.Length); Array.Clear(protectedBytes, 0, protectedBytes.Length); if (File.Exists(temp)) File.Delete(temp); }
    }
}

static class BotRuntime
{
    const string DefaultV1ApiUrl = "http://127.0.0.1:4096";
    const string DefaultV2ApiUrl = "http://127.0.0.1:49374";
    static string DefaultApiUrl(string version) { return version == "v2" ? DefaultV2ApiUrl : DefaultV1ApiUrl; }
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
        string version=settings.OpenCodeServerVersion=="v2"?"v2":"v1";
        string engineRoot=Path.GetFullPath(Path.Combine(ProductPaths.Home,"opencode-runtime"));
        string configured=String.IsNullOrWhiteSpace(settings.OpenCodeExecutable)?"":Path.GetFullPath(settings.OpenCodeExecutable.Trim());
        string selected=Path.Combine(engineRoot,version,"node_modules",".bin","opencode.cmd");
        if(!String.IsNullOrWhiteSpace(configured))
        {
            bool isManaged=configured.StartsWith(engineRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
            if(isManaged) return File.Exists(selected)?selected:"";
            if(File.Exists(configured)) return configured;
        }
        return File.Exists(selected)?selected:"";
    }
    public static string BuildOpenCodePath(DesktopSettings settings,string executableOverride=null)
    {
        string executable=String.IsNullOrWhiteSpace(executableOverride)?ResolveOpenCodeExecutable(settings):executableOverride;
        string executableDirectory=String.IsNullOrWhiteSpace(executable)?"":Path.GetDirectoryName(executable);
        string bun=ResolveBunExecutable();string bunDirectory=String.IsNullOrWhiteSpace(bun)?"":Path.GetDirectoryName(bun);
        return ProductPaths.BotModulesBin+Path.PathSeparator+executableDirectory+Path.PathSeparator+bunDirectory+Path.PathSeparator+Path.GetDirectoryName(ProductPaths.Node)+Path.PathSeparator+(Environment.GetEnvironmentVariable("PATH")??"");
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
                if(!candidates.Contains(fullPath,StringComparer.OrdinalIgnoreCase)) candidates.Add(fullPath);
                if(isRunning) running.Add(fullPath);
            }
            catch { }
        };
        add(settings.OpenCodeExecutable,false);
        foreach(string version in new[]{"v1","v2"}) add(Path.Combine(ProductPaths.Home,"opencode-runtime",version,"node_modules",".bin","opencode.cmd"),false);
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
                if(major!=1&&major!=2) continue;
                detected.Add(new OpenCodeInstallation
                {
                    ApiVersion=major==2?"v2":"v1",
                    Version=ParseOpenCodeVersion(output).ToString(),
                    Executable=executable,
                    IsRunning=running.Contains(executable),
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
        string version=settings.OpenCodeServerVersion=="v2"?"v2":"v1";
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
        string version=settings.OpenCodeServerVersion??"v1";
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
    public static Uri StartLocalV2Service(DesktopSettings settings)
    {
        if(settings.OpenCodeServerVersion!="v2"||!IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings)))throw new InvalidOperationException("A local OpenCode V2 URL is required to start the managed V2 service.");
        EnsureLocalV2Password(settings);
        string executable=ResolveOpenCodeExecutable(settings);
        if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable))throw new OpenCodeInstallRequiredException(InstallOpenCodeHint(settings,"v2"));

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
        string output=RunOpenCodeCommand(settings,executable,"service","start");
        Uri actual=null;
        foreach(string line in (output??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string candidate=line.Trim();
            Uri parsed;
            if(Uri.TryCreate(candidate,UriKind.Absolute,out parsed)&&parsed.Scheme==Uri.UriSchemeHttp&&parsed.IsLoopback){actual=new Uri(parsed.GetLeftPart(UriPartial.Authority));break;}
        }
        if(actual==null)throw new InvalidOperationException("OpenCode V2 service started, but its actual HTTP URL was not returned by `opencode service start`: "+output);

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
                throw new InvalidOperationException("The V2 service rejected the password configured by its service manager. The Bot was not started.",ex);
            throw new InvalidOperationException("Could not verify the authenticated V2 service at "+actual+": "+ex.Message,ex);
        }
        settings.OpenCodeUrl=actual.ToString().TrimEnd('/');
        SettingsStore.Save(settings);
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
        StartLocalV2Service(settings);
    }
    public static string GetDefaultApiUrl(string version) { return DefaultApiUrl(version); }
    static string FindOnPath(string executableName)
    {
        foreach(string directory in GetSearchPathEntries())
        {
            string candidate=Path.Combine(directory,executableName);
            if(File.Exists(candidate)) return candidate;
        }
        return "";
    }
    static string ResolveNodeExecutable()
    {
        if(File.Exists(ProductPaths.Node)) return ProductPaths.Node;
        return FindOnPath("node.exe");
    }
    static string ResolveBunExecutable()
    {
        string bundled=Path.Combine(ProductPaths.Home,"dependencies","bun","bun.exe");
        if(File.Exists(bundled)) return bundled;
        string found=FindOnPath("bun.exe");
        if(!String.IsNullOrWhiteSpace(found)) return found;
        string userBun=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".bun","bin","bun.exe");
        return File.Exists(userBun)?userBun:"";
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
        env["TELEGRAM_BOT_TOKEN"] = s.BotToken ?? "";
        env["TELEGRAM_ALLOWED_USER_ID"] = s.AllowedUserId ?? "";
        env["BOT_LOCALE"] = s.Locale ?? "zh";
        env["OPENCODE_SERVER_VERSION"] = s.OpenCodeServerVersion ?? "v1";
        env["OPENCODE_API_URL"] = ResolveOpenCodeUrl(s);
        env["OPENCODE_SERVER_USERNAME"] = s.OpenCodeUsername ?? "opencode";
        env["OPENCODE_SERVER_PASSWORD"] = s.OpenCodePassword ?? "";
        if(s.OpenCodeServerVersion=="v2") env["OPENCODE_PASSWORD"] = s.OpenCodePassword ?? "";
        env["OPENCODE_MODEL_PROVIDER"] = s.ModelProvider ?? "opencode";
        env["OPENCODE_MODEL_ID"] = s.ModelId ?? "big-pickle";
        env["OPENCODE_TELEGRAM_EOT10_ENABLED"] = s.Eot10Enabled && !String.IsNullOrEmpty(s.Eot10Password) ? "1" : "0";
        env["OPENCODE_TELEGRAM_EOT10_PASSWORD"] = s.Eot10Enabled ? (s.Eot10Password ?? "") : "";
        env["OPENCODE_AUTO_RESTART_ENABLED"] = "true";
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
        if(IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings))&&File.Exists(ResolveOpenCodeExecutable(settings))) EnsureOpenCodeDependencies(settings.OpenCodeServerVersion=="v2"?"v2":"v1",progress,settings.Locale=="en");
        EnsureOpenCodeInstalled(settings);
        var stopped = StopCompetingBotProcesses();
        var stoppedOpenCode = StopOpenCodeProcesses();
        if(settings.OpenCodeServerVersion=="v2"&&IsLocalOpenCodeUrl(ResolveOpenCodeUrl(settings)))
        {
            Uri serviceUrl=StartLocalV2Service(settings);
            if(progress!=null)progress((settings.Locale=="en"?"Authenticated OpenCode V2 service ready at ":"OpenCode V2 服务已验证并就绪：")+serviceUrl);
        }
        string result = Execute("start --daemon --mode installed", settings, 60);
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
                    if(!process.WaitForExit(10000)) throw new InvalidOperationException("Timed out stopping OpenCode process PID="+pid+".");
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
                    if(!process.WaitForExit(10000)) throw new InvalidOperationException("Timed out stopping OpenCode process PID="+pid+".");
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
    public static string InstallOpenCode(DesktopSettings settings, string apiVersion,Action<string> progress=null)
    {
        if(apiVersion!="v1"&&apiVersion!="v2") throw new ArgumentException("OpenCode API version must be v1 or v2.","apiVersion");
        Directory.CreateDirectory(ProductPaths.Home);
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
        if(apiVersion!="v1"&&apiVersion!="v2") throw new ArgumentException("OpenCode API version must be v1 or v2.","apiVersion");
        string engineHome=Path.Combine(ProductPaths.Home,"opencode-runtime",apiVersion);
        if(!Directory.Exists(engineHome)) return;
        StopOpenCodeProcessesUnder(engineHome);
        Directory.Delete(engineHome,true);
        if(Directory.Exists(engineHome)) throw new IOException("OpenCode API "+apiVersion+" install folder still exists after uninstall.");
    }
}

static class StartupSetting
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "OpenCodeTelegram";
    static string Command { get { return "\"" + Path.Combine(ProductPaths.Root, "OpenCodeTelegram.exe") + "\" --start-silent"; } }
    public static bool IsEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false)) return key != null && String.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static void Set(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
        }
    }
}

static class AppStartupSetting
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "OpenCodeTelegramApp";
    static string Command { get { return "\"" + Path.Combine(ProductPaths.Root, "OpenCodeTelegram.exe") + "\""; } }
    public static bool IsEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false)) return key != null && String.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static void Set(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
        }
    }
}

static class ProductRegistration
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenCodeTelegram";
    public static void Register(string installPath)
    {
        string exe = Path.Combine(installPath, "OpenCodeTelegram.exe");
        string uninstaller=Path.Combine(installPath,"Uninstall.exe");
        if(!File.Exists(uninstaller)&&File.Exists(exe)) File.Copy(exe,uninstaller);
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            bool isFirstRegistration=key.GetValue("InstallDate")==null;
            key.SetValue("DisplayName", ProductPaths.AppName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", "1.0.0", RegistryValueKind.String);
            key.SetValue("Publisher", ProductPaths.AppName, RegistryValueKind.String);
            key.SetValue("InstallLocation", installPath, RegistryValueKind.String);
            key.SetValue("DisplayIcon", exe, RegistryValueKind.String);
            key.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --uninstall --quiet", RegistryValueKind.String);
            if(isFirstRegistration) key.SetValue("InstallDate",DateTime.Now.ToString("yyyyMMdd"),RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }
    public static void Remove() { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
}

static class Cli
{
    public const string Usage = "opencode_telegram_bot\n\n" +
      "Usage: opencode-telegram <command> [options]\n\n" +
      "Commands:\n" +
      "  gui                         Open the desktop control panel\n" +
      "  start [--daemon]            Start the Telegram Bot\n" +
      "  stop                        Stop the Telegram Bot\n" +
      "  status                      Show Bot status\n" +
      "  configure bot               Bind/update the Telegram Bot interactively\n" +
      "  configure eot10 enable|disable  Configure EOT10 encryption\n" +
      "  configure opencode          Configure OpenCode endpoint and model\n" +
      "  configure show              Show non-secret configuration\n" +
      "  opencode install [v1|v2]    Install a local OpenCode engine\n" +
      "  opencode scan               List detected OpenCode installations\n" +
      "  opencode uninstall [v1|v2]  Remove an app-managed engine\n" +
      "  opencode select v1|v2 [path] Select API version / CLI executable\n" +
      "  config set <key> <value>    Update a GUI setting (use --env NAME for secrets)\n" +
      "  config unset <secret/path> Remove an optional secret or executable path\n" +
      "  app-autostart enable|disable|status  Control desktop sign-in startup\n" +
      "  language zh|en              Set the desktop language\n" +
      "  logs [--lines N]            Read recent Bot logs\n" +
      "  web start|stop|status|open  Manage the selected version's web server\n" +
      "  autostart enable|disable    Control Windows sign-in startup\n" +
      "  autostart status            Show startup setting\n" +
      "  --uninstall                 Uninstall this desktop application\n" +
      "  environment                 Show installed runtime paths (no secrets)\n" +
      "  help                        Show this help\n\n" +
      "Configuration: GUI settings are protected using Windows CurrentUser DPAPI.\n" +
      "Environment: OPENCODE_TELEGRAM_ROOT, OPENCODE_TELEGRAM_HOME, PATH.\n" +
      "Version: 1.0.0";
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] == "gui") { new DesktopApp().RunGui(); return 0; }
        if (args[0] == "--uninstall") return Uninstall(args.Skip(1).Contains("--quiet"));
        if (args[0] == "help" || args[0] == "--help" || args[0] == "-h") { Console.WriteLine(Usage); return 0; }
        if (args[0] == "version" || args[0] == "--version") { Console.WriteLine("1.0.0"); return 0; }
        if (args[0] == "configure") return Configure(args.Skip(1).ToArray());
        if (args[0] == "config" && args.Length > 1 && args[1] == "set") return SetConfig(args.Skip(2).ToArray());
        if (args[0] == "config" && args.Length > 1 && args[1] == "unset") return UnsetConfig(args.Skip(2).ToArray());
        if (args[0] == "opencode") return OpenCodeCommand(args.Skip(1).ToArray());
        if (args[0] == "web") return WebCommand(args.Skip(1).ToArray());
        if (args[0] == "app-autostart") return AppAutostart(args.Skip(1).ToArray());
        if (args[0] == "language") return SetLanguage(args.Skip(1).ToArray());
        if (args[0] == "logs") return ShowLogs(args.Skip(1).ToArray());
        if (args[0] == "autostart")
        {
            if (args.Length == 1 || args[1] == "status") { Console.WriteLine(StartupSetting.IsEnabled() ? "enabled" : "disabled"); return 0; }
            if (args[1] == "enable") { StartupSetting.Set(true); Console.WriteLine("Autostart enabled."); return 0; }
            if (args[1] == "disable") { StartupSetting.Set(false); Console.WriteLine("Autostart disabled."); return 0; }
            Console.Error.WriteLine(Usage); return 2;
        }
        if (args[0] == "environment")
        {
            Console.WriteLine("OPENCODE_TELEGRAM_ROOT=" + ProductPaths.Root);
            Console.WriteLine("OPENCODE_TELEGRAM_HOME=" + ProductPaths.Home);
            Console.WriteLine("Node=" + ProductPaths.Node);
            Console.WriteLine("Bot CLI=" + ProductPaths.BotCli);
            return 0;
        }
        try
        {
            var s = SettingsStore.Load();
            if (args[0] == "status") Console.WriteLine(BotRuntime.Status(s));
            else if (args[0] == "start") Console.WriteLine(BotRuntime.Start(s,message=>Console.Error.WriteLine(message)));
            else if (args[0] == "stop") Console.WriteLine(BotRuntime.Stop(s));
            else { Console.Error.WriteLine(Usage); return 2; }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    static int SetConfig(string[] args)
    {
        if(args.Length<2||args.Length>3||(args.Length==3&&args[1]!="--env")){Console.Error.WriteLine("Use: opencode-telegram config set <key> <value> OR config set <secret-key> --env NAME");return 2;}
        string key=args[0],value=args[1];
        if(args.Length==3&&args[1]=="--env"){value=Environment.GetEnvironmentVariable(args[2]);if(value==null){Console.Error.WriteLine("Environment variable is not set: "+args[2]);return 2;}}
        var s=SettingsStore.Load();
        switch(key.ToLowerInvariant())
        {
            case "bot-token":s.BotToken=value;break;
            case "user-id":s.AllowedUserId=value;break;
            case "eot10.enabled":if(value!="true"&&value!="false"){Console.Error.WriteLine("Value must be true or false.");return 2;}s.Eot10Enabled=value=="true";break;
            case "eot10.password":if(value.Length<12){Console.Error.WriteLine("EOT10 password must contain at least 12 characters.");return 2;}s.Eot10Password=value;break;
            case "opencode.version":if(value!="v1"&&value!="v2"){Console.Error.WriteLine("Version must be v1 or v2.");return 2;}s.OpenCodeServerVersion=value;if(String.IsNullOrWhiteSpace(s.OpenCodeExecutable)||Path.GetFullPath(s.OpenCodeExecutable).StartsWith(Path.GetFullPath(Path.Combine(ProductPaths.Home,"opencode-runtime"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))s.OpenCodeExecutable="";if(String.IsNullOrWhiteSpace(s.OpenCodeUrl)||s.OpenCodeUrl=="http://127.0.0.1:4096"||s.OpenCodeUrl=="http://127.0.0.1:49374")s.OpenCodeUrl=value=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096";break;
            case "opencode.url":s.OpenCodeUrl=value;break;
            case "opencode.username":s.OpenCodeUsername=value;break;
            case "opencode.password":s.OpenCodePassword=value;break;
            case "opencode.executable":s.OpenCodeExecutable=value;break;
            case "model.provider":s.ModelProvider=value;break;
            case "model.id":s.ModelId=value;break;
            default:Console.Error.WriteLine("Unknown setting key: "+key);return 2;
        }
        SettingsStore.Save(s);Console.WriteLine("Saved "+key+" (DPAPI protected).");return 0;
    }
    static int UnsetConfig(string[] args)
    {
        if(args.Length!=1){Console.Error.WriteLine("Use: opencode-telegram config unset eot10.password|opencode.password|opencode.executable|bot-token|user-id");return 2;}
        var s=SettingsStore.Load();switch(args[0].ToLowerInvariant())
        {
            case "eot10.password":s.Eot10Password="";s.Eot10Enabled=false;break;
            case "opencode.password":s.OpenCodePassword="";break;
            case "opencode.executable":s.OpenCodeExecutable="";break;
            case "bot-token":s.BotToken="";break;
            case "user-id":s.AllowedUserId="";break;
            default:Console.Error.WriteLine("This setting cannot be unset: "+args[0]);return 2;
        }
        SettingsStore.Save(s);Console.WriteLine("Removed "+args[0]+".");return 0;
    }

    static int OpenCodeCommand(string[] args)
    {
        var s=SettingsStore.Load();string version=s.OpenCodeServerVersion=="v2"?"v2":"v1";
        if(args.Length==0){Console.Error.WriteLine("Use: opencode-telegram opencode install|scan|uninstall|select");return 2;}
        if(args[0]=="install")return InstallOpenCode(args.Length>1?args[1]:null);
        if(args[0]=="scan")
        {
            foreach(var item in BotRuntime.ScanOpenCodeInstallations(s))Console.WriteLine(item.ApiVersion+"\t"+item.Version+"\t"+item.Executable+(item.IsRunning?"\trunning":""));
            return 0;
        }
        if(args[0]=="uninstall")
        {
            string target=args.Length>1?args[1]:version;if(target!="v1"&&target!="v2"){Console.Error.WriteLine("Version must be v1 or v2.");return 2;}
            BotRuntime.UninstallOpenCode(target);if(s.OpenCodeServerVersion==target){s.OpenCodeExecutable="";SettingsStore.Save(s);}Console.WriteLine("Removed app-managed OpenCode "+target+".");return 0;
        }
        if(args[0]=="select")
        {
            if(args.Length<2||(args[1]!="v1"&&args[1]!="v2")){Console.Error.WriteLine("Use: opencode-telegram opencode select v1|v2 [executable]");return 2;}
            s.OpenCodeServerVersion=args[1];s.OpenCodeExecutable=args.Length>2?args[2]:"";s.OpenCodeUrl=args[1]=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096";SettingsStore.Save(s);Console.WriteLine("Selected OpenCode "+args[1]+(String.IsNullOrEmpty(s.OpenCodeExecutable)?" (managed/default CLI)":" CLI: "+s.OpenCodeExecutable));return 0;
        }
        Console.Error.WriteLine("Unknown OpenCode command. Use: install|scan|uninstall|select");return 2;
    }

    static int AppAutostart(string[] args)
    {
        if(args.Length==0||args[0]=="status"){Console.WriteLine(AppStartupSetting.IsEnabled()?"enabled":"disabled");return 0;}
        if(args[0]=="enable"){AppStartupSetting.Set(true);Console.WriteLine("Desktop app autostart enabled.");return 0;}
        if(args[0]=="disable"){AppStartupSetting.Set(false);Console.WriteLine("Desktop app autostart disabled.");return 0;}
        Console.Error.WriteLine("Use: opencode-telegram app-autostart enable|disable|status");return 2;
    }
    static int SetLanguage(string[] args)
    {
        if(args.Length!=1||(args[0]!="zh"&&args[0]!="en")){Console.Error.WriteLine("Use: opencode-telegram language zh|en");return 2;}
        var s=SettingsStore.Load();s.Locale=args[0];SettingsStore.Save(s);Console.WriteLine("Language set to "+args[0]+".");return 0;
    }
    static int WebCommand(string[] args)
    {
        if(args.Length==0){Console.Error.WriteLine("Use: opencode-telegram web start|stop|status|open");return 2;}
        string pidFile=Path.Combine(ProductPaths.Home,"web-server.pid");var s=SettingsStore.Load();
        if(args[0]=="open")
        {
            if(s.OpenCodeServerVersion=="v2"&&BotRuntime.IsLocalOpenCodeUrl(BotRuntime.ResolveOpenCodeUrl(s)))BotRuntime.StartLocalV2Service(s);
            else if(s.OpenCodeServerVersion=="v2"){Uri remote=GetSelectedWebUrl(s);BotRuntime.OpenExternalBrowser(remote,s).GetAwaiter().GetResult();Console.WriteLine("Opened "+remote.GetLeftPart(UriPartial.Path));return 0;}
            else{int currentPid;if(TryReadWebProcess(pidFile,out currentPid)==null){int startResult=WebCommand(new[]{"start"});if(startResult!=0)return startResult;}s=SettingsStore.Load();}
            Uri url=GetSelectedWebUrl(s);BotRuntime.OpenExternalBrowser(url,s).GetAwaiter().GetResult();Console.WriteLine("Opened "+url.GetLeftPart(UriPartial.Path));return 0;
        }
        if(args[0]=="status")
        {
            if(s.OpenCodeServerVersion=="v2"&&BotRuntime.IsLocalOpenCodeUrl(BotRuntime.ResolveOpenCodeUrl(s))){string url=BotRuntime.GetLocalV2ServiceStatus(s);Console.WriteLine(url=="stopped"?"stopped":"running\tURL="+url);return 0;}
            if(s.OpenCodeServerVersion=="v2"){Console.WriteLine("configured\tURL="+GetWebUrl(s));return 0;}
            int pid;Process p=TryReadWebProcess(pidFile,out pid);Console.WriteLine(p==null?"stopped":"running\tPID="+pid+"\tURL="+GetWebUrl(s));return 0;
        }
        if(args[0]=="stop")
        {
            if(s.OpenCodeServerVersion=="v2"){Console.WriteLine(BotRuntime.StopLocalV2Service(s));return 0;}
            int pid;Process p=TryReadWebProcess(pidFile,out pid);if(p==null){TryDelete(pidFile);Console.WriteLine("Web service is not running.");return 0;}
            var kill=new ProcessStartInfo{FileName=Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe",Arguments="/d /c taskkill /PID "+pid+" /T /F",UseShellExecute=false,CreateNoWindow=true};using(var k=Process.Start(kill))k.WaitForExit(10000);TryDelete(pidFile);Console.WriteLine("Stopped web service (PID "+pid+").");return 0;
        }
        if(args[0]=="start")
        {
            if(s.OpenCodeServerVersion=="v2")
            {
                if(!BotRuntime.IsLocalOpenCodeUrl(BotRuntime.ResolveOpenCodeUrl(s))){Console.WriteLine("Using configured OpenCode V2 server at "+GetWebUrl(s));return 0;}
                Uri serviceUrl=BotRuntime.StartLocalV2Service(s);Console.WriteLine("Authenticated OpenCode V2 service is running at "+serviceUrl);return 0;
            }
            int existingPid; if(TryReadWebProcess(pidFile,out existingPid)!=null){Console.WriteLine("Web service is already running (PID "+existingPid+").");return 0;}
            string executable=BotRuntime.ResolveOpenCodeExecutable(s);if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable)){Console.Error.WriteLine("Selected OpenCode CLI is not installed. Use opencode-telegram opencode install first.");return 1;}
            string version=s.OpenCodeServerVersion=="v2"?"v2":"v1",verb=version=="v2"?"serve":"web",url=GetWebUrl(s);var uri=new Uri(url);
            BotRuntime.EnsureLocalV2Password(s);
            BotRuntime.EnsureOpenCodeDependencies(version,message=>Console.Error.WriteLine(message),s.Locale=="en");
            var psi=new ProcessStartInfo{WorkingDirectory=ProductPaths.Home,UseShellExecute=false,CreateNoWindow=true};
            if(Path.GetExtension(executable).Equals(".cmd",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(executable).Equals(".bat",StringComparison.OrdinalIgnoreCase)){psi.FileName=Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe";psi.Arguments="/d /s /c \"\""+executable+"\" "+verb+" --hostname "+uri.Host+" --port "+uri.Port+"\"";}else{psi.FileName=executable;psi.Arguments=verb+" --hostname "+uri.Host+" --port "+uri.Port;}
            psi.EnvironmentVariables["PATH"]=BotRuntime.BuildOpenCodePath(s,executable);psi.EnvironmentVariables["OPENCODE_SERVER_USERNAME"]=s.OpenCodeUsername??"opencode";psi.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"]=s.OpenCodePassword??"";if(version=="v2")psi.EnvironmentVariables["OPENCODE_PASSWORD"]=s.OpenCodePassword??"";Process server=Process.Start(psi);Directory.CreateDirectory(ProductPaths.Home);File.WriteAllText(pidFile,server.Id.ToString());Console.WriteLine("Started OpenCode "+version+" web service (PID "+server.Id+") at "+url);return 0;
        }
        Console.Error.WriteLine("Use: opencode-telegram web start|stop|status|open");return 2;
    }
    static string GetWebUrl(DesktopSettings s)
    {
        Uri url;
        if(!Uri.TryCreate(BotRuntime.ResolveOpenCodeUrl(s),UriKind.Absolute,out url))throw new InvalidOperationException("The configured OpenCode server URL is invalid.");
        return url.ToString();
    }
    static Uri GetSelectedWebUrl(DesktopSettings settings)
    {
        Uri address=new Uri(GetWebUrl(settings));
        TelegramWebContext context;
        return settings.ResolvedWebFollowMode!="off"&&TelegramWebContext.TryLoad(ProductPaths.Home,out context)?context.WebAddress(address,settings.OpenCodeServerVersion):address;
    }
    public static Process TryReadWebProcess(string pidFile,out int pid)
    {
        pid=0;try{if(!File.Exists(pidFile)||!Int32.TryParse(File.ReadAllText(pidFile).Trim(),out pid))return null;Process p=Process.GetProcessById(pid);return p.HasExited?null:p;}catch{return null;}
    }
    static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch{}}
    static int ShowLogs(string[] args)
    {
        int lines=100;if(args.Length==2&&args[0]=="--lines")Int32.TryParse(args[1],out lines);lines=Math.Max(1,Math.Min(lines,2000));
        string path=Path.Combine(ProductPaths.Home,"logs");if(!Directory.Exists(path)){Console.WriteLine("No logs found.");return 0;}
        string file=Directory.GetFiles(path,"*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();if(file==null){Console.WriteLine("No logs found.");return 0;}
        var tail=new Queue<string>();foreach(string line in File.ReadLines(file)){if(tail.Count>=lines)tail.Dequeue();tail.Enqueue(line);}foreach(string line in tail)Console.WriteLine(line);return 0;
    }

    static int Configure(string[] args)
    {
        var settings = SettingsStore.Load();
        if (args.Length == 0 || args[0] == "show")
        {
            Console.WriteLine("Bot token: " + (String.IsNullOrEmpty(settings.BotToken) ? "not bound" : "saved (hidden)"));
            Console.WriteLine("Allowed user ID: " + (settings.AllowedUserId ?? "not set"));
            Console.WriteLine("EOT10: " + (settings.Eot10Enabled ? "enabled" : "disabled"));
            Console.WriteLine("OpenCode: " + (settings.OpenCodeUrl ?? "http://127.0.0.1:4096"));
            Console.WriteLine("OpenCode API version: " + (settings.OpenCodeServerVersion ?? "v1"));
            Console.WriteLine("OpenCode executable: " + (String.IsNullOrEmpty(settings.OpenCodeExecutable)?"managed/default":settings.OpenCodeExecutable));
            Console.WriteLine("Model: " + (settings.ModelProvider ?? "opencode") + "/" + (settings.ModelId ?? "big-pickle"));
            Console.WriteLine("Bot autostart: " + (StartupSetting.IsEnabled() ? "enabled" : "disabled"));
            Console.WriteLine("Desktop autostart: " + (AppStartupSetting.IsEnabled() ? "enabled" : "disabled"));
            Console.WriteLine("Language: " + (settings.Locale ?? "zh"));
            return 0;
        }
        if (args[0] == "bot")
        {
            settings.BotToken = ReadSecret("Telegram Bot Token: ");
            settings.AllowedUserId = ReadValue("Allowed Telegram User ID: ", settings.AllowedUserId);
            settings.Locale = ReadValue("Bot locale [zh/en]", settings.Locale ?? "zh");
        }
        else if (args[0] == "eot10")
        {
            if (args.Length < 2 || (args[1] != "enable" && args[1] != "disable")) { Console.Error.WriteLine("Use: opencode-telegram configure eot10 enable|disable"); return 2; }
            settings.Eot10Enabled = args[1] == "enable";
            if (settings.Eot10Enabled)
            {
                string first = ReadSecret("EOT10 password (12+ characters): ");
                string second = ReadSecret("Repeat EOT10 password: ");
                if (first.Length < 12 || first != second) throw new InvalidOperationException("Password must be 12+ characters and both entries must match.");
                settings.Eot10Password = first;
            }
        }
        else if (args[0] == "opencode")
        {
            settings.OpenCodeServerVersion = ReadValue("OpenCode API version [v1/v2]", settings.OpenCodeServerVersion ?? "v1");
            settings.OpenCodeUrl = ReadValue("OpenCode API URL", settings.OpenCodeUrl ?? "http://127.0.0.1:4096");
            settings.OpenCodeUsername = ReadValue("OpenCode server username", settings.OpenCodeUsername ?? "opencode");
            settings.OpenCodePassword = ReadSecret("OpenCode server password (leave blank to keep current): ", true, settings.OpenCodePassword);
            settings.ModelProvider = ReadValue("Model provider", settings.ModelProvider ?? "opencode");
            settings.ModelId = ReadValue("Model ID", settings.ModelId ?? "big-pickle");
            settings.OpenCodeExecutable = ReadValue("OpenCode executable/shim path (optional)", settings.OpenCodeExecutable ?? "");
        }
        else { Console.Error.WriteLine("Unknown configuration section. Use: bot, eot10, opencode, show"); return 2; }
        SettingsStore.Save(settings);
        Console.WriteLine("Settings saved with current-user DPAPI protection.");
        return 0;
    }

    static string ReadValue(string prompt, string current)
    {
        Console.Write(prompt + (String.IsNullOrEmpty(current) ? ": " : " [" + current + "]: "));
        string value = Console.ReadLine();
        if(Console.IsInputRedirected)Console.WriteLine();
        return String.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    static string ReadSecret(string prompt, bool allowKeepCurrent = false, string current = "")
    {
        Console.Write(prompt);
        if(Console.IsInputRedirected)
        {
            string value=Console.ReadLine()??"";
            Console.WriteLine();
            return allowKeepCurrent&&value.Length==0?current:value;
        }
        var secret = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
            else if (!Char.IsControl(key.KeyChar)) secret.Append(key.KeyChar);
        }
        Console.WriteLine();
        return allowKeepCurrent && secret.Length == 0 ? current : secret.ToString();
    }

    static int InstallOpenCode(string requestedVersion=null)
    {
        var settings = SettingsStore.Load();
        try
        {
            if(!String.IsNullOrWhiteSpace(requestedVersion)){if(requestedVersion!="v1"&&requestedVersion!="v2")throw new InvalidOperationException("Version must be v1 or v2.");settings.OpenCodeServerVersion=requestedVersion;}
            settings.OpenCodeExecutable = BotRuntime.InstallOpenCode(settings,settings.OpenCodeServerVersion??"v1",message=>Console.Error.WriteLine(message));
            settings.OpenCodeUrl = settings.OpenCodeServerVersion == "v2" ? "http://127.0.0.1:49374" : "http://127.0.0.1:4096";
            BotRuntime.ConfigureSelectedV2Installation(settings,false);
            SettingsStore.Save(settings);
            Console.WriteLine("OpenCode installed: " + settings.OpenCodeExecutable);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("OpenCode install failed: " + ex.Message); return 1; }
    }

    static int Uninstall(bool quiet)
    {
        var uninstallSettings=SettingsStore.Load();
        string uninstallPrompt=uninstallSettings.Locale=="en"
            ? "Remove opencode_telegram_bot from this computer? Your saved Bot settings and logs will be kept."
            : "确定从此电脑卸载 opencode_telegram_bot 吗？已保存的机器人设置和日志会保留。";
        if (!quiet && MessageBox.Show(uninstallPrompt,ProductPaths.AppName,MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) return 0;
        string root = Path.GetFullPath(ProductPaths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try { BotRuntime.Execute("stop", SettingsStore.Load(), 20); } catch { }
        StartupSetting.Set(false);
        AppStartupSetting.Set(false);
        ProductRegistration.Remove();
        string startMenu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs");
        foreach(string folderName in new[]{"OpenCode Telegram","opencode_telegram_bot"})
            try { string programs=Path.Combine(startMenu,folderName); if(Directory.Exists(programs)) Directory.Delete(programs,true); } catch { }
        foreach(string linkName in new[]{"OpenCode Telegram.lnk","opencode_telegram_bot.lnk"})
            try { string desktopLink=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),linkName); if(File.Exists(desktopLink)) File.Delete(desktopLink); } catch { }
        Environment.SetEnvironmentVariable(ProductPaths.EnvRoot, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(ProductPaths.EnvHome, null, EnvironmentVariableTarget.User);
        string bin = Path.Combine(root, "bin");
        string userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var paths = userPath.Split(Path.PathSeparator).Where(p => !String.IsNullOrWhiteSpace(p) && !String.Equals(p.TrimEnd('\\','/'), bin.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH", String.Join(Path.PathSeparator.ToString(), paths), EnvironmentVariableTarget.User);
        DesktopAppBroadcast.EnvironmentChanged();
        var cleanup = new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/d /c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + root + "\"", WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, CreateNoWindow = true };
        Process.Start(cleanup);
        return 0;
    }
}

sealed class DesktopApp
{
    DesktopSettings settings;
    Window window;
    TextBox userIdBox, urlBox, modelProviderBox, modelIdBox, openCodePathBox;
    PasswordBox tokenBox, eotPasswordBox, serverPasswordBox;
    CheckBox eotEnabledBox, autoStartBox, autoStartAppBox;
    ComboBox serverVersionBox, languagePicker;
    TextBlock statusText;
    TextBlock startupFeedback;
    TextBlock apiVersionHint;
    TextBlock openCodeFeedback;
    TextBox logView;
    Button serviceToggle;
    Button installV1Button, installV2Button, uninstallV1Button, uninstallV2Button, scanOpenCodeButton;
    bool openCodeOperationRunning;
    DispatcherTimer logTimer;
    string activeServiceLogPath;
    bool statusRefreshRunning;
    StackPanel content;
    StackPanel sidebar;
    Button activeNav;
    string[] navLabels;
    readonly List<Button> navButtons = new List<Button>();
    TextBlock sidebarFooterText;
    int selectedPage;
    TextBox cliInput, cliOutput;
    Microsoft.Web.WebView2.Wpf.WebView2 embeddedBrowser;
    bool webConnectionRunning;
    Process cliProcess;
    PasswordBox cliSecretInput;
    Button cliRunButton,cliStopButton;
    readonly List<string> cliHistory=new List<string>();
    int cliHistoryIndex;
    string cliTranscript="";
    Process webServerProcess;
    TextBlock webStatus;
    Button webStartButton, webStopButton;
    DispatcherTimer webContextTimer;
    TelegramWebContext telegramWebContext=new TelegramWebContext();
    TextBlock webContextStatus;
    Button webFollowButton;
    Uri connectedWebServer;
    bool webContextRefreshRunning;
    readonly List<RadioButton> webFollowOptions=new List<RadioButton>();
    bool webFollowModeUpdating;
    TextBlock webFollowFeedback,webContextHint;
    Uri lastWebAddress;
    bool webContextAvailable;
    string webPendingTarget;
    bool webFollowChoicesVisible=true;
    TextBlock webFollowModeHeading;

    bool shutdownRunning,shutdownComplete;
    public void RunGui()
    {
        settings = SettingsStore.Load();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        window = CreateWindow();
        window.Closing+=async(s,e)=>
        {
            if(shutdownComplete)return;
            e.Cancel=true;
            if(shutdownRunning)return;
            shutdownRunning=true;
            if(logTimer!=null)logTimer.Stop();
            if(webContextTimer!=null)webContextTimer.Stop();
            await StopCliCommand();
            window.IsEnabled=false;
            SetStatus(settings.Locale=="en"?"Stopping all services before exit…":"正在停止所有服务，完成后退出…",false);
            while(serviceActionRunning||openCodeOperationRunning||webConnectionRunning)await Task.Delay(200);
            try
            {
                await Task.Run(()=>
                {
                    try{BotRuntime.Stop(settings);}catch{BotRuntime.StopCompetingBotProcesses();}
                    if(settings.OpenCodeServerVersion=="v2")try{BotRuntime.StopLocalV2Service(settings);}catch{}
                    BotRuntime.StopOpenCodeProcesses();
                });
                shutdownComplete=true;window.Close();
            }
            catch(Exception ex)
            {
                shutdownRunning=false;window.IsEnabled=true;
                SetStatus((settings.Locale=="en"?"Unable to stop services: ":"停止服务失败：")+ex.Message,true);
            }
        };
        app.MainWindow = window;
        app.Run(window);
    }

    Window CreateWindow()
    {
        var w = new Window { Width = 1120, Height = 760, MinWidth = 980, MinHeight = 680, WindowStartupLocation = WindowStartupLocation.CenterScreen, WindowStyle = WindowStyle.SingleBorderWindow, Background = new SolidColorBrush(Color.FromRgb(246, 247, 251)), ResizeMode = ResizeMode.CanResize, Title = ProductPaths.AppName };
        var shell = new Border { Background = new SolidColorBrush(Color.FromRgb(246, 247, 251)) };
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) }); root.RowDefinitions.Add(new RowDefinition());
        shell.Child = root; w.Content = shell;
        var titlebar = new Grid { Margin = new Thickness(22, 0, 18, 0) };
        titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Label(ProductPaths.AppName, 14, true, Color.FromRgb(53, 58, 72)); title.VerticalAlignment = VerticalAlignment.Center; titlebar.Children.Add(title); Grid.SetColumn(title, 0);
        languagePicker=new ComboBox{Width=142,Height=32,VerticalAlignment=VerticalAlignment.Center,ItemsSource=new string[]{"简体中文","English"},SelectedIndex=settings.Locale=="en"?1:0,ToolTip="Language / 界面语言"};
        languagePicker.SelectionChanged+=(s,e)=>{if(languagePicker.SelectedIndex<0)return;string locale=languagePicker.SelectedIndex==1?"en":"zh";if(settings.Locale==locale)return;settings.Locale=locale;try{SettingsStore.Save(settings);ApplyLanguage();ShowPage(selectedPage);}catch(Exception ex){MessageBox.Show("无法保存语言设置："+ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);}};
        titlebar.Children.Add(languagePicker); Grid.SetColumn(languagePicker,1);
        titlebar.MouseLeftButtonDown += (s,e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
        root.Children.Add(titlebar); Grid.SetRow(titlebar, 0);

        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); body.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(body, 1); root.Children.Add(body);
        sidebar = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(239, 241, 247)), Margin = new Thickness(0, 0, 1, 0) };
        var brand = new StackPanel { Margin = new Thickness(22, 22, 16, 28) };
        brand.Children.Add(Label(ProductPaths.AppName, 16, true, Color.FromRgb(35, 41, 56)));
        sidebar.Children.Add(brand);
        navLabels = new string[] { "概览", "机器人账号", "加密设置", "OpenCode 引擎", "启动设置", "Bot CLI", "OpenCode 网页端", "语言与外观" };
        for (int i = 0; i < navLabels.Length; i++) { int page = i; var b = NavButton(Ui(navLabels[i]), i == 0); b.Click += (s,e) => ShowPage(page); sidebar.Children.Add(b); navButtons.Add(b); if (i == 0) activeNav = b; }
        var sideFooter = new StackPanel { Margin = new Thickness(22, 0, 16, 20), VerticalAlignment = VerticalAlignment.Bottom };
        sidebarFooterText=Label("OpenCode 安装与启动器", 11, false, Color.FromRgb(129, 137, 157)); sideFooter.Children.Add(sidebarFooterText);
        sidebar.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { sideFooter } });
        body.Children.Add(sidebar); Grid.SetColumn(sidebar, 0);
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content = new StackPanel { Margin = new Thickness(36, 28, 36, 34) }; scroller.Content = content; body.Children.Add(scroller); Grid.SetColumn(scroller, 1);
        ShowPage(0);
        w.Loaded += async (s,e) => { if(selectedPage==0) await RefreshStatus(); };
        return w;
    }

    void ShowPage(int page)
    {
        selectedPage = page;
        if(logTimer!=null) logTimer.Stop();
        if(webContextTimer!=null)webContextTimer.Stop();
        content.Children.Clear();
        tokenBox=null;userIdBox=null;eotEnabledBox=null;eotPasswordBox=null;
        urlBox=null;serverVersionBox=null;modelProviderBox=null;modelIdBox=null;openCodePathBox=null;serverPasswordBox=null;
        if (activeNav != null) SetNavStyle(activeNav, false);
        foreach (var child in sidebar.Children) if (child is Button) SetNavStyle((Button)child, false);
        activeNav = sidebar.Children.OfType<Button>().ElementAt(page); SetNavStyle(activeNav, true);
        if(embeddedBrowser!=null){embeddedBrowser.Dispose();embeddedBrowser=null;}
        if (page == 0) { BuildDashboard(); logTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)}; logTimer.Tick+=async(s,e)=>await RefreshStatus(); logTimer.Start(); } else if (page == 1) BuildBotSettings(); else if (page == 2) BuildEncryptionSettings(); else if (page == 3) BuildOpenCodeSettings(); else if(page==4) BuildStartupSettings(); else if(page==5) BuildCliConsole(); else if(page==6) BuildOpenCodeWeb(); else if(page==7) BuildLanguageSettings();
    }

    void ApplyLanguage()
    {
        for(int i=0;i<navButtons.Count;i++) navButtons[i].Content=Ui(navLabels[i]);
        if(sidebarFooterText!=null) sidebarFooterText.Text=Ui("OpenCode 安装与启动器");
    }

    void BuildDashboard()
    {
        AddHeading("控制中心", "OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。");
        var card = Card(); var stack = new StackPanel(); card.Child = stack;
        stack.Children.Add(Label("运行状态", 13, true, Color.FromRgb(112, 120, 140)));
        statusText = Label("正在读取 Bot 状态…", 20, true, Color.FromRgb(39, 46, 65)); stack.Children.Add(statusText);
        var row = Row(); serviceToggle=ActionButton("启动服务", true); serviceToggle.MinWidth=150; var refresh = ActionButton("刷新状态", false);
        serviceToggle.Click += async (s,e) => await RunBotAction(serviceToggle.Tag as string??"start", true); refresh.Click += async (s,e) => await RefreshStatus();
        row.Children.Add(serviceToggle); row.Children.Add(refresh); stack.Children.Add(row); content.Children.Add(card);
        var logs=Card(); var logStack=new StackPanel(); logs.Child=logStack; var logHeader=Row(); logHeader.Children.Add(Label("运行日志",16,true,Color.FromRgb(39,46,65))); var reloadLog=ActionButton("刷新日志",false); reloadLog.Click+=(s,e)=>LoadLog(); logHeader.Children.Add(reloadLog); logStack.Children.Add(logHeader);
        logView=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas"),FontSize=12,Height=230,Background=new SolidColorBrush(Color.FromRgb(25,29,38)),Foreground=new SolidColorBrush(Color.FromRgb(220,226,238)),Padding=new Thickness(12)}; logStack.Children.Add(logView); content.Children.Add(logs); LoadLog();
        var hint = Card(); var hs = new StackPanel(); hint.Child=hs; hs.Children.Add(Label("快速开始",16,true,Color.FromRgb(39,46,65))); hs.Children.Add(Label("先绑定 Telegram Bot，再设置 OpenCode 地址和 EOT10 密码。所有凭据保存在当前 Windows 用户的 DPAPI 加密设置中。",13,false,Color.FromRgb(105,113,133))); content.Children.Add(hint);
    }

    void BuildBotSettings()
    {
        AddHeading("机器人账号", "绑定你自己的 Telegram Bot，并限制允许操作的用户。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        s.Children.Add(Label("获取 Telegram Bot Token",15,true,Color.FromRgb(39,46,65)));
        s.Children.Add(Label("1. 在 Telegram 搜索官方账号 @BotFather 并打开对话。\n2. 发送 /newbot，按提示设置 Bot 显示名称。\n3. 设置唯一用户名，必须以 bot 结尾（例如 my_opencode_bot）。\n4. BotFather 会返回一串 Token。复制后粘贴到下方 Bot Token 输入框；不要把 Token 发给任何人。",13,false,Color.FromRgb(83,91,111)));
        s.Children.Add(Label("获取你自己的 Telegram 用户 ID",15,true,Color.FromRgb(39,46,65)));
        s.Children.Add(Label("1. 在 Telegram 搜索 @userinfobot 并打开对话。\n2. 点击 Start 或发送 /start。\n3. 复制回复中的数字 ID，粘贴到下方允许的 Telegram User ID。\n请填写你个人账号的 ID，不是 Bot 的 ID。",13,false,Color.FromRgb(83,91,111)));
        s.Children.Add(Label("Bot Token",12,true,Color.FromRgb(83,91,111))); tokenBox=new PasswordBox { Password=settings.BotToken, Height=40, FontSize=14, Padding=new Thickness(12,8,12,8), Margin=new Thickness(0,6,0,14), ToolTip="从 BotFather 获取" }; s.Children.Add(tokenBox);
        s.Children.Add(Label("允许的 Telegram User ID",12,true,Color.FromRgb(83,91,111))); userIdBox=new TextBox { Text=settings.AllowedUserId, Height=40, FontSize=14, Padding=new Thickness(12,8,12,8), Margin=new Thickness(0,6,0,14) }; s.Children.Add(userIdBox);
        var save=ActionButton("保存机器人设置",true); save.Click+=(a,b)=>SaveSettings(true); s.Children.Add(save); content.Children.Add(card);
    }

    void BuildEncryptionSettings()
    {
        AddHeading("EOT10 加密", "在设备端和 Bot 之间加密消息正文与附件。密码使用当前 Windows 用户 DPAPI 保护。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        eotEnabledBox=new CheckBox { Content=Ui("启用 EOT10 加密"), IsChecked=settings.Eot10Enabled, FontSize=16, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,4,0,14) }; s.Children.Add(eotEnabledBox);
        s.Children.Add(Label("加密密码（至少 12 个字符）",12,true,Color.FromRgb(83,91,111))); eotPasswordBox=new PasswordBox { Password=settings.Eot10Password, Height=40, Padding=new Thickness(12,8,12,8), Margin=new Thickness(0,6,0,14), FontSize=14 }; s.Children.Add(eotPasswordBox);
        var save=ActionButton("保存加密设置",true); save.Click+=(a,b)=>SaveSettings(true);
        var remove=ActionButton("删除保存的密码并关闭 EOT10",false); remove.Click+=(a,b)=>{ settings.Eot10Enabled=false; settings.Eot10Password=""; eotEnabledBox.IsChecked=false; eotPasswordBox.Clear(); SettingsStore.Save(settings); SetStatus("已删除本机 EOT10 密码。",false); };
        var row=Row(); row.Children.Add(save); row.Children.Add(remove); s.Children.Add(row); content.Children.Add(card);
    }

    void BuildOpenCodeSettings()
    {
        settings.OpenCodeUrl=BotRuntime.ResolveOpenCodeUrl(settings);
        AddHeading("OpenCode 引擎", "连接现有 OpenCode，或安装独立的本地引擎。Bot 分叉本身不依赖上游 npm 包。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        s.Children.Add(Label(settings.Locale=="en"?"Before starting, all local OpenCode processes are stopped and the selected API version is started.":"启动服务时会先停止本机所有 OpenCode 进程，再按所选 API 版本启动服务。",12,false,Color.FromRgb(105,113,133)));
        s.Children.Add(Label("Server API 版本",12,true,Color.FromRgb(83,91,111))); serverVersionBox=new ComboBox { Height=36, Margin=new Thickness(0,5,0,12), ItemsSource=new string[]{"v1","v2"}, SelectedItem=settings.OpenCodeServerVersion??"v1" }; serverVersionBox.SelectionChanged+=(a,b)=>UpdateSelectedOpenCodeVersion(); s.Children.Add(serverVersionBox);
        apiVersionHint=Label("",12,false,Color.FromRgb(105,113,133));apiVersionHint.Margin=new Thickness(0,0,0,10);s.Children.Add(apiVersionHint);UpdateApiVersionHint();
        var advanced=new Expander { Header=settings.Locale=="en"?"Advanced settings":"高级设置", IsExpanded=false, Margin=new Thickness(0,4,0,8) };
        var advancedContent=new StackPanel { Margin=new Thickness(4,12,4,4) }; advanced.Content=advancedContent;
        advancedContent.Children.Add(Label("Server API URL",12,true,Color.FromRgb(83,91,111))); urlBox=new TextBox { Text=settings.OpenCodeUrl, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; advancedContent.Children.Add(urlBox);
        advancedContent.Children.Add(Label("可选：OpenCode 可执行文件（通常无需手动填写，点击扫描自动识别）",12,true,Color.FromRgb(83,91,111))); var exeRow=Row(); openCodePathBox=new TextBox { Text=settings.OpenCodeExecutable, Height=38, MinWidth=560, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,8,12) }; exeRow.Children.Add(openCodePathBox); var pickExe=ActionButton("浏览…",false); pickExe.Click+=(a,b)=>{var dialog=new OpenFileDialog{Filter="OpenCode executable|opencode.exe;opencode.cmd;*.exe;*.cmd|All files|*.*",CheckFileExists=true};if(dialog.ShowDialog()==true)openCodePathBox.Text=dialog.FileName;}; exeRow.Children.Add(pickExe); advancedContent.Children.Add(exeRow);
        scanOpenCodeButton=ActionButton(settings.Locale=="en"?"Scan and select existing OpenCode":"扫描并选择已有 OpenCode",false); scanOpenCodeButton.Click+=async(a,b)=>await ScanExistingOpenCode(); var scanRow=Row(); scanRow.Children.Add(scanOpenCodeButton); advancedContent.Children.Add(scanRow);
        advancedContent.Children.Add(Label("Server 用户名",12,true,Color.FromRgb(83,91,111))); var serverUser=new TextBox { Text=settings.OpenCodeUsername, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; serverUser.TextChanged+=(a,b)=>settings.OpenCodeUsername=serverUser.Text; advancedContent.Children.Add(serverUser);
        advancedContent.Children.Add(Label("Server 密码（V2 服务必填；可用 opencode service get password 查看）",12,true,Color.FromRgb(83,91,111))); serverPasswordBox=new PasswordBox { Password=settings.OpenCodePassword, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; advancedContent.Children.Add(serverPasswordBox);
        advancedContent.Children.Add(Label("模型 Provider",12,true,Color.FromRgb(83,91,111))); modelProviderBox=new TextBox { Text=settings.ModelProvider, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; advancedContent.Children.Add(modelProviderBox);
        advancedContent.Children.Add(Label("模型 ID",12,true,Color.FromRgb(83,91,111))); modelIdBox=new TextBox { Text=settings.ModelId, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,16) }; advancedContent.Children.Add(modelIdBox);
        var save=ActionButton("保存引擎设置",true); save.Click+=(a,b)=>SaveSettings(true);
        installV1Button=ActionButton("安装 OpenCode（API v1）",false); installV1Button.Click+=async(a,b)=>await InstallOpenCode("v1");
        installV2Button=ActionButton("安装 OpenCode（API v2）",false); installV2Button.Click+=async(a,b)=>await InstallOpenCode("v2");
        var installRow=Row(); installRow.Children.Add(save); installRow.Children.Add(installV1Button); installRow.Children.Add(installV2Button); advancedContent.Children.Add(installRow);
        uninstallV1Button=ActionButton(settings.Locale=="en"?"Uninstall OpenCode (API v1)":"卸载 OpenCode（API v1）",false); uninstallV1Button.Click+=async(a,b)=>await UninstallOpenCode("v1");
        uninstallV2Button=ActionButton(settings.Locale=="en"?"Uninstall OpenCode (API v2)":"卸载 OpenCode（API v2）",false); uninstallV2Button.Click+=async(a,b)=>await UninstallOpenCode("v2");
        var uninstallRow=Row(); uninstallRow.Children.Add(uninstallV1Button); uninstallRow.Children.Add(uninstallV2Button); advancedContent.Children.Add(uninstallRow);
        openCodeFeedback=Label("",13,false,Color.FromRgb(105,113,133)); openCodeFeedback.Margin=new Thickness(0,14,0,4); advancedContent.Children.Add(openCodeFeedback);
        s.Children.Add(advanced);
        var visibleSave=ActionButton("保存引擎设置",true);visibleSave.Click+=(a,b)=>SaveSettings(true);s.Children.Add(visibleSave);
        advancedContent.Children.Remove(openCodeFeedback);s.Children.Add(openCodeFeedback);
        UpdateOpenCodeEngineButtons(); content.Children.Add(card);
    }

    void UpdateSelectedOpenCodeVersion()
    {
        string next=serverVersionBox.SelectedItem as string??"v1";
        string currentUrl=urlBox!=null?urlBox.Text.Trim():settings.OpenCodeUrl;
        if(currentUrl=="http://127.0.0.1:4096"||currentUrl=="http://127.0.0.1:49374")
        {
            string defaultUrl=next=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096";
            if(urlBox!=null) urlBox.Text=defaultUrl;
            else settings.OpenCodeUrl=defaultUrl;
        }
        string currentExecutable=openCodePathBox!=null?openCodePathBox.Text.Trim():settings.OpenCodeExecutable;
        string managedRoot=Path.GetFullPath(Path.Combine(ProductPaths.Home,"opencode-runtime"))+Path.DirectorySeparatorChar;
        bool isManaged=String.IsNullOrWhiteSpace(currentExecutable);
        if(!isManaged)
        {
            try { isManaged=Path.GetFullPath(currentExecutable).StartsWith(managedRoot,StringComparison.OrdinalIgnoreCase); }
            catch { }
        }
        if(isManaged)
        {
            string selected=Path.Combine(ProductPaths.Home,"opencode-runtime",next,"node_modules",".bin","opencode.cmd");
            string selectedExecutable=File.Exists(selected)?selected:"";
            if(openCodePathBox!=null) openCodePathBox.Text=selectedExecutable;
            else settings.OpenCodeExecutable=selectedExecutable;
        }
        settings.OpenCodeServerVersion=next;
        UpdateApiVersionHint();
    }

    void BuildStartupSettings()
    {
        AddHeading("启动设置", settings.Locale=="en"?"Configure desktop app and Bot startup at Windows sign-in.":"分别设置桌面软件和 Bot 的 Windows 登录自启动。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        autoStartAppBox=new CheckBox { Content=Ui("登录 Windows 时启动桌面软件"), IsChecked=AppStartupSetting.IsEnabled(), FontSize=15, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,2,0,14) }; s.Children.Add(autoStartAppBox);
        autoStartBox=new CheckBox { Content=Ui("登录 Windows 时自动启动 Bot"), IsChecked=StartupSetting.IsEnabled(), FontSize=15, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,2,0,14) }; s.Children.Add(autoStartBox);
        var save=ActionButton("保存启动选项",true); save.Click+=(a,b)=>{ try { AppStartupSetting.Set(autoStartAppBox.IsChecked==true); StartupSetting.Set(autoStartBox.IsChecked==true); settings.AutoStartApp=autoStartAppBox.IsChecked==true; settings.AutoStart=autoStartBox.IsChecked==true; SettingsStore.Save(settings); startupFeedback.Text=settings.AutoStartApp||settings.AutoStart?"开机自启动选项已保存。":"开机自启动已关闭。"; startupFeedback.Foreground=new SolidColorBrush(Color.FromRgb(33,128,74)); } catch(Exception ex) { startupFeedback.Text="保存开机自启动设置失败："+ex.Message; startupFeedback.Foreground=new SolidColorBrush(Color.FromRgb(191,55,55)); } }; s.Children.Add(save);
        startupFeedback=Label("更改后点击保存。",12,false,Color.FromRgb(105,113,133)); startupFeedback.Margin=new Thickness(0,12,0,4); s.Children.Add(startupFeedback);
        content.Children.Add(card);
    }
    void BuildCliConsole()
    {
        AddHeading("Bot CLI",settings.Locale=="en"?"Built-in Bot console: Enter to run, Up/Down for history. This is not a PowerShell shell.":"应用内 Bot 命令行：回车执行，上下键选择历史命令。无需打开外部窗口；这里不是 PowerShell。");
        var terminal=Card(); var ts=new DockPanel(); terminal.Child=ts;
        cliInput=new TextBox{Width=380,Height=38,FontFamily=new FontFamily("Consolas"),FontSize=13,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,8,8,0),ToolTip=Ui("例如：status、config set model.id big-pickle 或 web status")};
        cliSecretInput=new PasswordBox{Width=380,Height=38,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,8,8,0),Visibility=Visibility.Collapsed,ToolTip=settings.Locale=="en"?"Interactive response (hidden; Enter submits)":"交互回答（隐藏输入，回车提交）"};
        cliRunButton=ActionButton(settings.Locale=="en"?"Run / Submit":"执行 / 提交",true);cliRunButton.Click+=async(a,b)=>await RunDesktopCliCommand();
        cliStopButton=ActionButton(settings.Locale=="en"?"Stop command":"停止命令",false);cliStopButton.IsEnabled=cliProcess!=null;cliStopButton.Click+=async(a,b)=>await StopCliCommand();
        var commandRow=Row(); commandRow.Children.Add(cliInput);commandRow.Children.Add(cliSecretInput); commandRow.Children.Add(cliRunButton);commandRow.Children.Add(cliStopButton); DockPanel.SetDock(commandRow,Dock.Bottom); ts.Children.Add(commandRow);
        cliOutput=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas"),FontSize=12,Height=280,Background=new SolidColorBrush(Color.FromRgb(25,29,38)),Foreground=new SolidColorBrush(Color.FromRgb(220,226,238)),Padding=new Thickness(12),Margin=new Thickness(0,12,0,0)}; ts.Children.Add(cliOutput);
        cliOutput.Text=cliTranscript;
        cliHistoryIndex=cliHistory.Count;
        cliInput.KeyDown+=async(a,b)=>{if(b.Key==Key.Enter){b.Handled=true;await RunDesktopCliCommand();}else if(b.Key==Key.Up||b.Key==Key.Down){b.Handled=true;cliHistoryIndex=Math.Max(0,Math.Min(cliHistory.Count,cliHistoryIndex+(b.Key==Key.Up?-1:1)));cliInput.Text=cliHistoryIndex<cliHistory.Count?cliHistory[cliHistoryIndex]:"";cliInput.CaretIndex=cliInput.Text.Length;}};
        cliSecretInput.KeyDown+=async(a,b)=>{if(b.Key==Key.Enter){b.Handled=true;await RunDesktopCliCommand();}};
        SetCliInputMode();
        content.Children.Add(terminal);
        var help=Card();var details=new StackPanel();help.Child=details;
        details.Children.Add(Label(settings.Locale=="en"?"Supported commands":"支持的命令",16,true,Color.FromRgb(39,46,65)));
        details.Children.Add(Label(settings.Locale=="en"?"Enter commands without a prefix, or use opencode-telegram / opencode_telegram_bot. Interactive configure responses are hidden and not saved to history. clear clears output; Ctrl+C in the input stops the current command.":"直接输入命令即可，也支持 opencode-telegram / opencode_telegram_bot 前缀。configure 交互回答使用隐藏输入，不写入历史。clear 清空输出；输入框内 Ctrl+C 停止当前命令。",13,false,Color.FromRgb(83,91,111)));
        details.Children.Add(new TextBox{Text=Cli.Usage,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=12,BorderThickness=new Thickness(0),Background=Brushes.Transparent});
        if(settings.Locale!="en")details.Children.Add(Label("Bot：start 启动，stop 停止，status 查看状态，logs [--lines N] 查看日志。\n交互配置：configure bot 设置机器人账号；configure eot10 enable|disable 设置加密；configure opencode 设置引擎和模型；configure show 查看非敏感配置。\n设置：config set <key> <value> 修改配置；config unset <key> 删除可选配置。\n引擎：opencode install [v1|v2] 安装，scan 扫描，select v1|v2 [path] 选择，uninstall [v1|v2] 删除应用管理的引擎。\n网页：web start|stop|status|open 启动、停止、查看或打开网页服务。\n登录启动：autostart enable|disable|status 控制 Bot；app-autostart enable|disable|status 控制桌面软件。\n其他：language zh|en 切换语言，environment 查看运行路径，help 查看帮助，version 查看版本；gui 打开桌面窗口；--uninstall 卸载桌面软件。",13,false,Color.FromRgb(83,91,111)));
        details.Children.Add(Label("config set keys: bot-token, user-id, eot10.enabled, eot10.password, opencode.version, opencode.url, opencode.username, opencode.password, opencode.executable, model.provider, model.id\nconfig unset: bot-token, user-id, eot10.password, opencode.password, opencode.executable",12,false,Color.FromRgb(83,91,111)));
        content.Children.Add(help);
        cliInput.PreviewKeyDown+=(a,b)=>{if(b.Key==Key.C&&(Keyboard.Modifiers&ModifierKeys.Control)!=0&&cliProcess!=null){b.Handled=true;cliStopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}};
        cliSecretInput.PreviewKeyDown+=(a,b)=>{if(b.Key==Key.C&&(Keyboard.Modifiers&ModifierKeys.Control)!=0&&cliProcess!=null){b.Handled=true;cliStopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}};
        cliInput.Focus();
    }

    void SetCliInputMode()
    {
        if(cliInput==null||cliSecretInput==null)return;
        bool active=cliProcess!=null;
        cliInput.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        cliSecretInput.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        cliStopButton.IsEnabled=active;
        if(active)cliSecretInput.Focus();else cliInput.Focus();
    }
    async Task StopCliCommand()
    {
        var process=cliProcess;
        if(process==null)return;
        try
        {
            if(process.HasExited)return;
            var info=new ProcessStartInfo{FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"taskkill.exe"),Arguments="/PID "+process.Id+" /T /F",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var stop=Process.Start(info))
            {
                var stdout=stop.StandardOutput.ReadToEndAsync();var stderr=stop.StandardError.ReadToEndAsync();
                await Task.Run(()=>stop.WaitForExit());await Task.WhenAll(stdout,stderr);
                if(stop.ExitCode!=0&&!process.HasExited)process.Kill();
            }
        }
        catch(Exception ex){AppendCliOutput(ex.Message);}
    }

    async Task RunDesktopCliCommand()
    {
        if(cliInput==null||cliOutput==null)return;
        if(cliProcess!=null)
        {
            try{await cliProcess.StandardInput.WriteLineAsync(cliSecretInput.Password);await cliProcess.StandardInput.FlushAsync();cliSecretInput.Clear();}
            catch(Exception ex){AppendCliOutput(ex.Message);}
            return;
        }
        string command=cliInput.Text.Trim(); if(command.Length==0)return;
        cliInput.Clear();
        if(command.StartsWith("opencode-telegram ",StringComparison.OrdinalIgnoreCase))command=command.Substring("opencode-telegram ".Length).TrimStart();
        if(command.StartsWith("opencode_telegram_bot ",StringComparison.OrdinalIgnoreCase))command=command.Substring("opencode_telegram_bot ".Length).TrimStart();
        if(command=="clear"){cliTranscript="";cliOutput.Clear();return;}
        bool secret=Regex.IsMatch(command,@"(?i)^config\s+set\s+(bot-token|eot10\.password|opencode\.password)\b");
        if(!secret){cliHistory.Add(command);cliHistoryIndex=cliHistory.Count;}
        string executable=Process.GetCurrentProcess().MainModule.FileName;
        AppendCliOutput("> opencode-telegram "+(secret?"config set [secret hidden]":command));
        try
        {
            Directory.CreateDirectory(ProductPaths.Home);
            var psi=new ProcessStartInfo{FileName=executable,Arguments="--embedded-cli "+command,WorkingDirectory=ProductPaths.Home,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(psi))
            {
                cliProcess=process;SetCliInputMode();
                Task stdout=ReadCliStream(process.StandardOutput),stderr=ReadCliStream(process.StandardError);
                await Task.Run(()=>process.WaitForExit());
                await Task.WhenAll(stdout,stderr);
                AppendCliOutput(Ui("退出代码：")+process.ExitCode);
                settings=SettingsStore.Load();
            }
        }
        catch(Exception ex){AppendCliOutput(Ui("命令执行失败：")+ex.Message);}
        finally{cliProcess=null;SetCliInputMode();}
    }
    async Task ReadCliStream(StreamReader reader)
    {
        var buffer=new char[1024];int count;
        while((count=await reader.ReadAsync(buffer,0,buffer.Length))>0)
        {
            string text=new string(buffer,0,count);
            await window.Dispatcher.InvokeAsync(()=>AppendCliText(text));
        }
    }
    void AppendCliText(string text){cliTranscript+=text;if(cliOutput==null)return;cliOutput.AppendText(text);cliOutput.ScrollToEnd();}
    void AppendCliOutput(string text){AppendCliText(text+Environment.NewLine);}

    void BuildOpenCodeWeb()
    {
        if(webServerProcess==null){int runningPid;webServerProcess=Cli.TryReadWebProcess(Path.Combine(ProductPaths.Home,"web-server.pid"),out runningPid);}
        string version=settings.OpenCodeServerVersion=="v2"?"v2":"v1";
        Uri address; string url=BotRuntime.ResolveOpenCodeUrl(settings);if(!Uri.TryCreate(url,UriKind.Absolute,out address))address=new Uri(version=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096");
        connectedWebServer=null;
        webContextAvailable=false;
        webPendingTarget=null;
        bool en=settings.Locale=="en";
        AddHeading("OpenCode 网页端",en?"Your desktop workspace for shared conversations and uninterrupted work.":"在电脑继续对话与工作，自由选择是否跟随 Telegram 会话。");
        var controls=Card();controls.Padding=new Thickness(18,14,18,14);var panel=new StackPanel();controls.Child=panel;
        var connectionHeader=new Grid();connectionHeader.ColumnDefinitions.Add(new ColumnDefinition());connectionHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var endpoint=Label("OpenCode "+version.ToUpperInvariant()+"  ·  "+address,13,true,Color.FromRgb(59,67,87));endpoint.TextWrapping=TextWrapping.NoWrap;endpoint.TextTrimming=TextTrimming.CharacterEllipsis;endpoint.ToolTip=address.ToString();connectionHeader.Children.Add(endpoint);
        webStatus=Label(en?"Connecting…":"正在连接…",12,false,Color.FromRgb(105,113,133));webStatus.MaxWidth=310;webStatus.Margin=new Thickness(18,0,0,0);Grid.SetColumn(webStatus,1);connectionHeader.Children.Add(webStatus);panel.Children.Add(connectionHeader);
        var row=new WrapPanel{Margin=new Thickness(0,8,0,0)};
        webStartButton=ActionButton(settings.Locale=="en"?"Reconnect":"重新连接",true);webStartButton.Click+=async(a,b)=>await StartOpenCodeWeb(version,address);
        var webRefresh=ActionButton("刷新网页",false);webRefresh.Click+=async(a,b)=>{if(embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null)embeddedBrowser.CoreWebView2.Reload();else await StartOpenCodeWeb(version,address);};
        var openExternal=ActionButton("在默认浏览器打开",false);openExternal.Click+=async(a,b)=>await OpenExternalWeb(version,address);
        webStopButton=ActionButton("停止网页服务",false);webStopButton.IsEnabled=webServerProcess!=null&&!webServerProcess.HasExited;webStopButton.Click+=(a,b)=>StopOpenCodeWeb();row.Children.Add(webStartButton);row.Children.Add(webRefresh);row.Children.Add(openExternal);row.Children.Add(webStopButton);
        foreach(Button button in row.Children){button.Padding=new Thickness(13,7,13,7);button.Margin=new Thickness(0,0,8,4);}
        panel.Children.Add(row);content.Children.Add(controls);
        var contextCard=Card();var contextPanel=new StackPanel();contextCard.Child=contextPanel;
        contextCard.Padding=new Thickness(18,16,18,16);
        var modeHeader=new Grid();modeHeader.ColumnDefinitions.Add(new ColumnDefinition());modeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});modeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        webFollowModeHeading=Label("",15,true,Color.FromRgb(39,46,65));modeHeader.Children.Add(webFollowModeHeading);
        webFollowFeedback=Label(en?"Saved automatically":"选择后自动保存",11,false,Color.FromRgb(105,113,133));webFollowFeedback.Margin=new Thickness(10,3,10,0);Grid.SetColumn(webFollowFeedback,1);modeHeader.Children.Add(webFollowFeedback);
        var choices=new Grid{Margin=new Thickness(0,8,0,12),Visibility=webFollowChoicesVisible?Visibility.Visible:Visibility.Collapsed};webFollowOptions.Clear();
        var toggleChoices=ActionButton(en?(webFollowChoicesVisible?"Collapse":"Options"):(webFollowChoicesVisible?"收起选项":"展开选项"),false);toggleChoices.Padding=new Thickness(8,3,8,3);toggleChoices.Margin=new Thickness(0,0,0,8);Grid.SetColumn(toggleChoices,2);modeHeader.Children.Add(toggleChoices);
        toggleChoices.Click+=(a,b)=>{webFollowChoicesVisible=!webFollowChoicesVisible;choices.Visibility=webFollowChoicesVisible?Visibility.Visible:Visibility.Collapsed;toggleChoices.Content=en?(webFollowChoicesVisible?"Collapse":"Options"):(webFollowChoicesVisible?"收起选项":"展开选项");};contextPanel.Children.Add(modeHeader);
        string[] modes={"off","prompt","force"};
        string[] titles=en?new[]{"Don't follow","Prompt to follow","Force follow"}:new[]{"不跟随","提示跟随","强制跟随"};
        string[] descriptions=en?new[]{"Browse independently. Opens your last page or the home page; Telegram switches have no effect.","Opens Telegram's session on entry. Later switches only show a notice; click Follow to switch.","Always follows Telegram's session, including automatic switches. Unsent desktop input may be lost."}:new[]{"独立浏览，进入时打开上次页面或首页；Telegram 切换不会影响网页。","进入时打开 Telegram 会话；之后只提示，点击“跟随”才切换，保护未发送输入。","始终跟随 Telegram 当前会话，自动切换；可能丢失电脑尚未发送的输入。"};
        for(int i=0;i<modes.Length;i++)
        {
            choices.ColumnDefinitions.Add(new ColumnDefinition());
            var optionContent=new StackPanel();optionContent.Children.Add(Label(titles[i],14,true,Color.FromRgb(39,46,65)));optionContent.Children.Add(Label(descriptions[i],12,false,Color.FromRgb(105,113,133)));
            var option=new RadioButton{GroupName="TelegramWebFollow",Tag=modes[i],Content=optionContent,Padding=new Thickness(10),Margin=new Thickness(i==0?0:8,0,0,0),VerticalContentAlignment=VerticalAlignment.Top,HorizontalContentAlignment=HorizontalAlignment.Stretch,MinHeight=106,Cursor=Cursors.Hand};
            option.IsChecked=settings.ResolvedWebFollowMode==modes[i];
            option.Checked+=(a,b)=>{if(!webFollowModeUpdating)ChangeWebFollowMode((string)((RadioButton)a).Tag);};
            var optionBorder=new Border{CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),Child=option};optionBorder.Margin=option.Margin;option.Margin=new Thickness(0);
            Grid.SetColumn(optionBorder,i);choices.Children.Add(optionBorder);webFollowOptions.Add(option);
        }
        UpdateWebFollowOptionStyles();contextPanel.Children.Add(choices);
        var contextRow=new Grid();contextRow.ColumnDefinitions.Add(new ColumnDefinition());contextRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        webContextStatus=Label(en?"Reading Telegram project and session…":"正在读取 Telegram 当前项目与会话…",12,false,Color.FromRgb(83,91,111));webContextStatus.TextWrapping=TextWrapping.NoWrap;webContextStatus.TextTrimming=TextTrimming.CharacterEllipsis;webContextStatus.MinHeight=36;contextRow.Children.Add(webContextStatus);
        webFollowButton=ActionButton(settings.Locale=="en"?"Follow Telegram session":"跟随 Telegram 当前会话",true);webFollowButton.IsEnabled=false;
        webFollowButton.Padding=new Thickness(13,8,13,8);webFollowButton.Margin=new Thickness(12,0,0,0);webFollowButton.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(webFollowButton,1);contextRow.Children.Add(webFollowButton);
        webFollowButton.Click+=async(a,b)=>
        {
            await RefreshTelegramWebContext();
            if(!webContextAvailable||!telegramWebContext.HasSession)return;
            if(connectedWebServer!=null&&embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null)NavigateWeb(connectedWebServer,true);
            else await StartOpenCodeWeb(version,address);
        };
        contextPanel.Children.Add(contextRow);webContextHint=Label("",11,false,Color.FromRgb(105,113,133));webContextHint.Margin=new Thickness(0,6,0,0);contextPanel.Children.Add(webContextHint);content.Children.Add(contextCard);
        var browserCard=Card();browserCard.Padding=new Thickness(8);embeddedBrowser=new Microsoft.Web.WebView2.Wpf.WebView2{Height=580,MinHeight=360};browserCard.Child=embeddedBrowser;content.Children.Add(browserCard);
        var browser=embeddedBrowser;
        browser.SourceChanged+=(a,b)=>{if(embeddedBrowser==browser){Uri current;if(Uri.TryCreate(browser.CoreWebView2.Source,UriKind.Absolute,out current)&&(current.Scheme=="http"||current.Scheme=="https"))lastWebAddress=new Uri(current.GetLeftPart(UriPartial.Path)+current.Fragment);UpdateTelegramWebContextStatus();}};
        browser.NavigationCompleted+=(a,b)=>{if(embeddedBrowser!=browser)return;webPendingTarget=null;if(!b.IsSuccess)webStatus.Text=(settings.Locale=="en"?"Web page failed to load: ":"网页加载失败：")+b.WebErrorStatus+(settings.Locale=="en"?". Reconnect or open in your default browser.":"。请重新连接或在默认浏览器打开。");};
        browser.Loaded+=async(a,b)=>{if(embeddedBrowser==browser){await RefreshTelegramWebContext();if(embeddedBrowser==browser)await StartOpenCodeWeb(version,address);}};
        webContextTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
        webContextTimer.Tick+=async(a,b)=>await RefreshTelegramWebContext();webContextTimer.Start();
    }

    void UpdateWebFollowOptionStyles()
    {
        bool en=settings.Locale=="en";
        string title=settings.ResolvedWebFollowMode=="off"?(en?"Don't follow":"不跟随"):settings.ResolvedWebFollowMode=="force"?(en?"Force follow":"强制跟随"):(en?"Prompt to follow":"提示跟随");
        if(webFollowModeHeading!=null)webFollowModeHeading.Text=(en?"Telegram follow · ":"Telegram 跟随 · ")+title;
        foreach(var option in webFollowOptions)
        {
            bool selected=(string)option.Tag==settings.ResolvedWebFollowMode;
            var border=option.Parent as Border;
            if(border==null)continue;
            border.Background=new SolidColorBrush(selected?Color.FromRgb(237,243,255):Color.FromRgb(248,249,252));
            border.BorderBrush=new SolidColorBrush(selected?Color.FromRgb(92,139,232):Color.FromRgb(230,232,238));
        }
    }
    void ChangeWebFollowMode(string mode)
    {
        string previous=settings.WebFollowMode;
        settings.WebFollowMode=mode;
        try
        {
            SettingsStore.Save(settings);
            webFollowFeedback.Text=settings.Locale=="en"?"Saved · applies immediately":"已保存 · 立即生效";
            webFollowFeedback.Foreground=new SolidColorBrush(Color.FromRgb(33,128,74));
            webFollowFeedback.ToolTip=null;
        }
        catch(Exception ex)
        {
            settings.WebFollowMode=previous;
            webFollowModeUpdating=true;
            foreach(var option in webFollowOptions)option.IsChecked=(string)option.Tag==settings.ResolvedWebFollowMode;
            webFollowModeUpdating=false;
            webFollowFeedback.Text=settings.Locale=="en"?"Not saved":"保存失败";
            webFollowFeedback.Foreground=new SolidColorBrush(Color.FromRgb(191,55,55));
            webFollowFeedback.ToolTip=ex.Message;
        }
        UpdateWebFollowOptionStyles();UpdateTelegramWebContextStatus();ApplyForcedWebFollow();
    }
    void ApplyForcedWebFollow()
    {
        if(settings.ResolvedWebFollowMode!="force"||!webContextAvailable||!telegramWebContext.HasSession||webConnectionRunning||shutdownRunning||selectedPage!=6||connectedWebServer==null||embeddedBrowser==null||embeddedBrowser.CoreWebView2==null)return;
        Uri current;
        Uri target=telegramWebContext.WebAddress(connectedWebServer,settings.OpenCodeServerVersion);
        if(webPendingTarget==target.GetLeftPart(UriPartial.Path))return;
        if(!Uri.TryCreate(embeddedBrowser.CoreWebView2.Source,UriKind.Absolute,out current)||current.GetLeftPart(UriPartial.Path)!=target.GetLeftPart(UriPartial.Path))NavigateWeb(connectedWebServer,true);
    }
    async Task RefreshTelegramWebContext()
    {
        if(selectedPage!=6)return;
        while(webContextRefreshRunning)await Task.Delay(25);
        if(selectedPage!=6)return;
        webContextRefreshRunning=true;
        var status=webContextStatus;
        try
        {
            TelegramWebContext context=null;
            bool loaded=await Task.Run(()=>TelegramWebContext.TryLoad(ProductPaths.Home,out context));
            if(selectedPage!=6||webContextStatus!=status)return;
            webContextAvailable=loaded;
            if(!loaded)
            {
                webContextStatus.Text=settings.Locale=="en"?"Telegram context is temporarily unavailable.":"Telegram 项目与会话状态暂时不可用。";
                webContextHint.Text=settings.Locale=="en"?"Your current page is kept. Retrying automatically; no switch will occur until state is available.":"保留当前网页并自动重试，读取恢复前不会跟随切换。";
                webFollowButton.IsEnabled=false;
                return;
            }
            telegramWebContext=context;
            UpdateTelegramWebContextStatus();
            ApplyForcedWebFollow();
        }
        finally{webContextRefreshRunning=false;}
    }
    void UpdateTelegramWebContextStatus()
    {
        if(selectedPage!=6||webContextStatus==null)return;
        bool en=settings.Locale=="en";
        string mode=settings.ResolvedWebFollowMode;
        webFollowButton.Visibility=mode=="prompt"?Visibility.Visible:Visibility.Collapsed;
        string hint=mode=="off"?(en?"Independent browsing · Telegram switches do not change this page.":"独立浏览 · Telegram 切换不会影响当前网页。"):
            mode=="force"?(en?"Always follows Telegram · Unsent desktop input may be lost when switching.":"始终跟随 Telegram · 切换时电脑未发送的输入可能丢失。"):
            (en?"Switch notices only · Your current input is kept until you click Follow.":"仅提示切换 · 点击“跟随”前，保留当前网页和输入。");
        string text;
        if(!telegramWebContext.HasSession)
        {
            string directory=telegramWebContext.Project==null?null:telegramWebContext.Project.Directory;
            text=(en?"Telegram project: ":"Telegram 项目：")+(String.IsNullOrWhiteSpace(directory)?(en?"Not selected":"未选择"):directory)+Environment.NewLine+(en?"Telegram session: Not selected":"Telegram 会话：未选择");
            if(mode!="off")hint=en?"No selected Telegram session. Use /projects, then /sessions or /new; this page is kept.":"Telegram 尚未选择会话。请使用 /projects、/sessions 或 /new；保留当前网页，不另建会话。";
            webFollowButton.IsEnabled=false;
        }
        else
        {
            Uri current;
            Uri target=telegramWebContext.WebAddress(connectedWebServer??new Uri(BotRuntime.ResolveOpenCodeUrl(settings)),settings.OpenCodeServerVersion);
            bool same=embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null&&Uri.TryCreate(embeddedBrowser.CoreWebView2.Source,UriKind.Absolute,out current)&&current.GetLeftPart(UriPartial.Path)==target.GetLeftPart(UriPartial.Path);
            string title=String.IsNullOrWhiteSpace(telegramWebContext.Session.Title)?telegramWebContext.Session.Id:telegramWebContext.Session.Title;
            text=(en?"Telegram project: ":"Telegram 项目：")+telegramWebContext.Session.Directory+Environment.NewLine+(en?"Telegram session: ":"Telegram 会话：")+title;
            if(same&&mode=="prompt")hint=en?"Same session · Prompts and model replies update live; type in the workspace to continue.":"同一会话 · 指令与模型回答实时更新，可在下方工作区继续输入。";
            else if(mode=="prompt")hint=en?"Telegram is on a different session · Click Follow to switch; your draft is kept until then.":"Telegram 当前会话与网页不同 · 点击“跟随”切换，切换前保留电脑输入。";
            webFollowButton.IsEnabled=mode=="prompt"&&webContextAvailable&&!same&&!webConnectionRunning;
        }
        if(webContextStatus.Text!=text)webContextStatus.Text=text;
        webContextStatus.ToolTip=text;
        if(webContextHint.Text!=hint)webContextHint.Text=hint;
        webContextHint.Foreground=new SolidColorBrush(mode=="force"?Color.FromRgb(173,101,26):Color.FromRgb(105,113,133));
    }

    async Task StartOpenCodeWeb(string version,Uri address)
    {
        if(shutdownRunning||webConnectionRunning||embeddedBrowser==null)return;
        webConnectionRunning=true;
        var browser=embeddedBrowser;
        webStartButton.IsEnabled=false;
        if(webFollowButton!=null)webFollowButton.IsEnabled=false;
        webStatus.Text=settings.Locale=="en"?"Connecting to OpenCode…":"正在连接 OpenCode…";
        try
        {
            if(browser.CoreWebView2==null)
            {
                var environment=await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null,Path.Combine(ProductPaths.Home,"webview2"));
                if(embeddedBrowser!=browser)return;
                await browser.EnsureCoreWebView2Async(environment);
                if(embeddedBrowser!=browser)return;
                browser.CoreWebView2.BasicAuthenticationRequested+=(sender,eventArgs)=>
                {
                    Uri target;
                    Uri configured=new Uri(BotRuntime.ResolveOpenCodeUrl(settings));
                    if(Uri.TryCreate(eventArgs.Uri,UriKind.Absolute,out target)&&target.GetLeftPart(UriPartial.Authority)==configured.GetLeftPart(UriPartial.Authority))
                    {
                        eventArgs.Response.UserName=settings.OpenCodeUsername??"opencode";
                        eventArgs.Response.Password=settings.OpenCodePassword??"";
                    }
                    else eventArgs.Cancel=true;
                };
            }
            await StartOpenCodeWebCore(version,address,browser);
        }
        catch(Exception ex){if(embeddedBrowser==browser)webStatus.Text=(settings.Locale=="en"?"Browser initialization failed (check Edge WebView2 Runtime): ":"浏览器初始化失败（请检查 Edge WebView2 Runtime）：")+ex.Message;}
        finally
        {
            webConnectionRunning=false;
            UpdateTelegramWebContextStatus();
            ApplyForcedWebFollow();
            if(embeddedBrowser!=null&&webStartButton!=null)webStartButton.IsEnabled=true;
            if(embeddedBrowser!=null&&embeddedBrowser!=browser&&selectedPage==6&&!shutdownRunning)
                QueueWebReconnect();
        }
    }
    void QueueWebReconnect()
    {
        window.Dispatcher.BeginInvoke(new Action(async()=>
        {
            if(selectedPage==6&&embeddedBrowser!=null&&!shutdownRunning)
                await StartOpenCodeWeb(settings.OpenCodeServerVersion=="v2"?"v2":"v1",new Uri(BotRuntime.ResolveOpenCodeUrl(settings)));
        }));
    }
    void NavigateWeb(Uri address,bool followTelegram=false)
    {
        Uri target=settings.ResolvedWebFollowMode=="off"&&!followTelegram?address:telegramWebContext.WebAddress(address,settings.OpenCodeServerVersion);
        if(settings.ResolvedWebFollowMode=="off"&&!followTelegram&&lastWebAddress!=null&&lastWebAddress.GetLeftPart(UriPartial.Authority)==address.GetLeftPart(UriPartial.Authority))target=lastWebAddress;
        Uri current;
        if(!followTelegram&&connectedWebServer!=null&&embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null&&Uri.TryCreate(embeddedBrowser.CoreWebView2.Source,UriKind.Absolute,out current)&&current.GetLeftPart(UriPartial.Authority)==address.GetLeftPart(UriPartial.Authority))
            target=new Uri(current.GetLeftPart(UriPartial.Path)+current.Fragment);
        connectedWebServer=address;
        webPendingTarget=target.GetLeftPart(UriPartial.Path);
        if(embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null)embeddedBrowser.CoreWebView2.Navigate(BotRuntime.AddWebAuthToken(target,settings).ToString());
        UpdateTelegramWebContextStatus();
    }
    async Task StartOpenCodeWebCore(string version,Uri address,Microsoft.Web.WebView2.Wpf.WebView2 browser)
    {
        if(!BotRuntime.IsLocalOpenCodeUrl(address.ToString()))
        {
            webStatus.Text=Ui("使用已配置的远程 OpenCode 服务：")+address;
            NavigateWeb(address);
            return;
        }
        if(version=="v2"&&BotRuntime.IsLocalOpenCodeUrl(BotRuntime.ResolveOpenCodeUrl(settings)))
        {
            try
            {
                Uri serviceUrl=await Task.Run(()=>BotRuntime.StartLocalV2Service(settings));
                if(embeddedBrowser!=browser)return;
                address=serviceUrl;webStopButton.IsEnabled=true;webStatus.Text=Ui("网页服务运行中：")+serviceUrl;
                if(serverPasswordBox!=null)serverPasswordBox.Password=settings.OpenCodePassword??"";
                NavigateWeb(serviceUrl);
                if(urlBox!=null)urlBox.Text=BotRuntime.ResolveOpenCodeUrl(settings);
            }
            catch(Exception ex){if(embeddedBrowser==browser)webStatus.Text=Ui("无法启动网页服务：")+ex.Message;}
            return;
        }
        string executable=BotRuntime.ResolveOpenCodeExecutable(settings);
        if(String.IsNullOrWhiteSpace(executable)||!File.Exists(executable)){webStatus.Text=Ui("所选版本的 CLI 尚未安装。请先到 OpenCode 引擎页面安装或扫描。");return;}
        if(webServerProcess!=null&&!webServerProcess.HasExited){NavigateWeb(address);return;}
        if(await ProbeWebService(address))
        {
            if(embeddedBrowser!=browser)return;
            webStatus.Text=Ui("网页服务运行中：")+address;
            NavigateWeb(address);
            return;
        }
        try
        {
            await Task.Run(()=>BotRuntime.EnsureOpenCodeDependencies(version,message=>window.Dispatcher.BeginInvoke(new Action(()=>{if(webStatus!=null)webStatus.Text=Ui(message);})),settings.Locale=="en"));
            if(embeddedBrowser!=browser)return;
            BotRuntime.EnsureLocalV2Password(settings);
            if(serverPasswordBox!=null)serverPasswordBox.Password=settings.OpenCodePassword??"";
            string verb=version=="v2"?"serve":"web";
            var psi=new ProcessStartInfo{WorkingDirectory=ProductPaths.Home,UseShellExecute=false,CreateNoWindow=true};
            if(Path.GetExtension(executable).Equals(".cmd",StringComparison.OrdinalIgnoreCase)){psi.FileName=Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe";psi.Arguments="/d /s /c \"\""+executable+"\" "+verb+" --hostname "+address.Host+" --port "+address.Port+"\"";}
            else {psi.FileName=executable;psi.Arguments=verb+" --hostname "+address.Host+" --port "+address.Port;}
            psi.EnvironmentVariables["PATH"]=BotRuntime.BuildOpenCodePath(settings,executable);
            psi.EnvironmentVariables["OPENCODE_SERVER_USERNAME"]=settings.OpenCodeUsername??"opencode";
            psi.EnvironmentVariables["OPENCODE_SERVER_PASSWORD"]=settings.OpenCodePassword??"";
            if(version=="v2")psi.EnvironmentVariables["OPENCODE_PASSWORD"]=settings.OpenCodePassword??"";
            webServerProcess=Process.Start(psi);Directory.CreateDirectory(ProductPaths.Home);File.WriteAllText(Path.Combine(ProductPaths.Home,"web-server.pid"),webServerProcess.Id.ToString());webServerProcess.EnableRaisingEvents=true;webServerProcess.Exited+=(a,b)=>window.Dispatcher.BeginInvoke(new Action(()=>{try{File.Delete(Path.Combine(ProductPaths.Home,"web-server.pid"));}catch{}if(webStatus!=null)webStatus.Text=Ui("网页服务已停止。");if(webStopButton!=null)webStopButton.IsEnabled=false;}));
            webStopButton.IsEnabled=true;webStatus.Text=Ui("正在启动网页服务…");
            bool ready=false;for(int i=0;i<40;i++){if(webServerProcess.HasExited||embeddedBrowser!=browser)break;ready=await ProbeWebService(address);if(ready)break;await Task.Delay(350);}
            if(embeddedBrowser!=browser)return;
            if(ready){webStatus.Text=Ui("网页服务运行中：")+address;NavigateWeb(address);}else webStatus.Text=webServerProcess.HasExited?Ui("网页服务启动失败，请检查所选版本和端口。"):Ui("服务仍在启动；可稍后点击网页区域刷新。");
        }
        catch(Exception ex){webStatus.Text=Ui("无法启动网页服务：")+ex.Message;}
    }
    Task<bool> ProbeWebService(Uri address)
    {
        string username=settings.OpenCodeUsername??"opencode",password=settings.OpenCodePassword??"";
        return Task.Run(()=>
        {
            try
            {
                var request=(HttpWebRequest)WebRequest.Create(address);request.Timeout=1000;request.ReadWriteTimeout=1000;request.Proxy=null;
                if(!String.IsNullOrWhiteSpace(password))request.Headers[HttpRequestHeader.Authorization]="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes(username+":"+password));
                using(var response=(HttpWebResponse)request.GetResponse())return (int)response.StatusCode<400;
            }
            catch{return false;}
        });
    }
    async Task OpenExternalWeb(string version,Uri address)
    {
        try
        {
            if(version=="v2"&&BotRuntime.IsLocalOpenCodeUrl(address.ToString()))address=await Task.Run(()=>BotRuntime.StartLocalV2Service(settings));
            else if(address.IsLoopback&&(webServerProcess==null||webServerProcess.HasExited))await StartOpenCodeWeb(version,address);
            await RefreshTelegramWebContext();
            Uri server=version=="v2"?new Uri(BotRuntime.ResolveOpenCodeUrl(settings)):address;
            Uri target=settings.ResolvedWebFollowMode=="off"?server:telegramWebContext.WebAddress(server,version);
            Uri current;
            if(embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null&&Uri.TryCreate(embeddedBrowser.CoreWebView2.Source,UriKind.Absolute,out current)&&current.GetLeftPart(UriPartial.Authority)==server.GetLeftPart(UriPartial.Authority))target=new Uri(current.GetLeftPart(UriPartial.Path)+current.Fragment);
            await BotRuntime.OpenExternalBrowser(target,settings);
        }
        catch(Exception ex){if(webStatus!=null)webStatus.Text=Ui("无法打开默认浏览器：")+ex.Message;}
    }
    async void StopOpenCodeWeb()
    {
        try
        {
            if(settings.OpenCodeServerVersion=="v2"&&BotRuntime.IsLocalOpenCodeUrl(BotRuntime.ResolveOpenCodeUrl(settings)))await Task.Run(()=>BotRuntime.StopLocalV2Service(settings));
            else if(webServerProcess!=null&&!webServerProcess.HasExited)
            {
                var kill=new ProcessStartInfo{FileName=Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe",Arguments="/d /c taskkill /PID "+webServerProcess.Id+" /T /F",UseShellExecute=false,CreateNoWindow=true};using(var p=Process.Start(kill))p.WaitForExit(5000);if(!webServerProcess.HasExited)webServerProcess.Kill();
            }
            try{File.Delete(Path.Combine(ProductPaths.Home,"web-server.pid"));}catch{}
            if(webStopButton!=null)webStopButton.IsEnabled=false;
            if(webStatus!=null)webStatus.Text=Ui("网页服务已停止。");
        }
        catch(Exception ex){if(webStatus!=null)webStatus.Text=Ui("停止网页服务失败：")+ex.Message;}
    }

    void UpdateApiVersionHint()
    {
        if(apiVersionHint==null)return;
        bool v2=serverVersionBox!=null&&serverVersionBox.SelectedItem as string=="v2";
        apiVersionHint.Text=settings.Locale=="en"
            ? (v2?"V2 requires a server password. For a local server, the app creates and securely saves one automatically; for an existing server, enter its password here.":"V1 connects to the classic OpenCode server (default port 4096).")
            : (v2?"V2 服务需要密码。本机服务由应用自动生成并加密保存；连接已有服务时，请在下方填写该服务密码。":"V1 连接经典 OpenCode 服务（默认端口 4096）。");
    }

    void BuildLanguageSettings()
    {
        AddHeading("语言与外观", "在窗口右上角切换简体中文或 English，选择后立即保存并应用。" );
        var card=Card();var s=new StackPanel();card.Child=s;
        s.Children.Add(Label("当前语言",14,true,Color.FromRgb(39,46,65)));
        s.Children.Add(Label(settings.Locale=="en"?"English":"简体中文",20,true,Color.FromRgb(43,103,230)));
        s.Children.Add(Label("使用窗口右上角的语言菜单切换。设置会保存在当前 Windows 用户的加密配置中。",13,false,Color.FromRgb(105,113,133)));
        content.Children.Add(card);
    }

    void AddHeading(string title,string subtitle) { var h=new StackPanel{Margin=new Thickness(0,0,0,22)}; h.Children.Add(Label(title,29,true,Color.FromRgb(34,40,56))); h.Children.Add(Label(subtitle,13,false,Color.FromRgb(111,119,138))); content.Children.Add(h); }
    Border Card() { return new Border { Background=Brushes.White, CornerRadius=new CornerRadius(18), Padding=new Thickness(22), Margin=new Thickness(0,0,0,16), BorderBrush=new SolidColorBrush(Color.FromRgb(230,232,238)), BorderThickness=new Thickness(1) }; }
    StackPanel Row() { return new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,10,0,0) }; }
    string Ui(string text)
    {
        if(settings==null||settings.Locale!="en") return text;
        if(text=="启动设置")return "Startup Settings";
        if(text=="Server 密码（V2 服务必填；可用 opencode service get password 查看）")return "Server password (required for V2; view with opencode service get password)";
        if(text=="正在读取 Bot 状态…")return "Reading Bot status…";
        if(text=="使用已配置的远程 OpenCode 服务：")return "Using configured remote OpenCode server: ";
        if(text=="命令已完成。")return "Command completed.";
        if(text=="OpenCode 安装与启动器") return "OpenCode installer and launcher";
        if(text=="OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。") return "An OpenCode installer and launcher with remote control via Telegram and encrypted communications.";
        if(text=="获取 Telegram Bot Token") return "Get a Telegram Bot Token";
        if(text=="1. 在 Telegram 搜索官方账号 @BotFather 并打开对话。\n2. 发送 /newbot，按提示设置 Bot 显示名称。\n3. 设置唯一用户名，必须以 bot 结尾（例如 my_opencode_bot）。\n4. BotFather 会返回一串 Token。复制后粘贴到下方 Bot Token 输入框；不要把 Token 发给任何人。") return "1. Search for the official @BotFather account on Telegram and open the chat.\n2. Send /newbot and follow the prompts to choose a display name.\n3. Choose a unique username ending in bot (for example, my_opencode_bot).\n4. Copy the token BotFather returns into the Bot Token field below. Never share this token.";
        if(text=="获取你自己的 Telegram 用户 ID") return "Get your Telegram user ID";
        if(text=="1. 在 Telegram 搜索 @userinfobot 并打开对话。\n2. 点击 Start 或发送 /start。\n3. 复制回复中的数字 ID，粘贴到下方允许的 Telegram User ID。\n请填写你个人账号的 ID，不是 Bot 的 ID。") return "1. Search for @userinfobot on Telegram and open the chat.\n2. Tap Start or send /start.\n3. Copy the numeric ID in its reply into Allowed Telegram User ID below.\nUse your personal account's ID, not the Bot's ID.";
        var words=new Dictionary<string,string>{{"概览","Overview"},{"机器人账号","Bot Account"},{"加密设置","Encryption"},{"OpenCode 引擎","OpenCode Engine"},{"启动与 CLI","Startup & CLI"},{"语言与外观","Language & Appearance"},{"控制中心","Control Center"},{"运行状态","Service Status"},{"启动机器人","Start Bot"},{"停止机器人","Stop Bot"},{"启动服务","Start service"},{"停止服务","Stop service"},{"刷新状态","Refresh Status"},{"快速开始","Quick Start"},{"保存机器人设置","Save Bot Settings"},{"保存加密设置","Save Encryption Settings"},{"登录 Windows 时自动启动 Bot","Start Bot at Windows sign-in"},{"登录 Windows 时启动桌面软件","Launch desktop app at Windows sign-in"},{"保存启动选项","Save Startup Settings"},{"安装 OpenCode","Install OpenCode"},{"安装 OpenCode（API v1）","Install OpenCode (API v1)"},{"安装 OpenCode（API v2）","Install OpenCode (API v2)"},{"保存引擎设置","Save Engine Settings"},{"运行日志","Runtime Log"},{"刷新日志","Refresh Log"},{"允许的 Telegram User ID","Allowed Telegram User ID"},{"加密密码（至少 12 个字符）","Encryption password (12+ characters)"},{"启用 EOT10 加密","Enable EOT10 encryption"},{"删除保存的密码并关闭 EOT10","Remove saved password and disable EOT10"},{"使用已安装 / 已运行的 OpenCode","Use existing OpenCode installation/server"},{"Server API 版本","Server API version"},{"Server API URL","Server API URL"},{"OpenCode 可执行文件（使用现有安装时可留空）","OpenCode executable (optional for existing installations)"},{"可选：OpenCode 可执行文件（通常无需手动填写，点击扫描自动识别）","Optional: OpenCode executable (usually detected automatically by scanning)"},{"扫描并选择已有 OpenCode","Scan and select existing OpenCode"},{"Server 用户名","Server username"},{"Server 密码","Server password"},{"Server 密码（可选；仅启用身份验证时填写）","Server password (optional; only if authentication is enabled)"},{"模型 Provider","Model provider"},{"模型 ID","Model ID"},{"EOT10 加密","EOT10 Encryption"},{"启动与命令行","Startup & Command Line"},{"独立桌面分叉","Standalone desktop app"},{"命令行（PowerShell / CMD）","Command line (PowerShell / CMD)"},{"设置已使用 Windows DPAPI 加密保存。","Settings saved securely with Windows DPAPI."},{"设置已成功保存。","Settings saved successfully."},{"开机自启动设置已保存。","Startup settings saved."},{"OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。","An OpenCode installer and launcher with remote control via Telegram and encrypted communications."},{"绑定你自己的 Telegram Bot，并限制允许操作的用户。","Connect your Telegram Bot and restrict which user can operate it."},{"获取 Telegram Bot Token","Get a Telegram Bot Token"},{"1. 在 Telegram 搜索官方账号 @BotFather 并打开对话。\n2. 发送 /newbot，按提示设置 Bot 显示名称。\n3. 设置唯一用户名，必须以 bot 结尾（例如 my_opencode_bot）。\n4. BotFather 会返回一串 Token。复制后粘贴到下方 Bot Token 输入框；不要把 Token 发给任何人。","1. Search for the official @BotFather account on Telegram and open the chat.\n2. Send /newbot and follow the prompts to choose a display name.\n3. Choose a unique username ending in bot (for example, my_opencode_bot).\n4. Copy the token BotFather returns into the Bot Token field below. Never share this token."},{"获取你自己的 Telegram 用户 ID","Get your Telegram user ID"},{"1. 在 Telegram 搜索 @userinfobot 并打开对话。\n2. 点击 Start 或发送 /start。\n3. 复制回复中的数字 ID，粘贴到下方允许的 Telegram User ID。\n请填写你个人账号的 ID，不是 Bot 的 ID。","1. Search for @userinfobot on Telegram and open the chat.\n2. Tap Start or send /start.\n3. Copy the numeric ID in its reply into Allowed Telegram User ID below.\nUse your personal account's ID, not the Bot's ID."},{"在设备端和 Bot 之间加密消息正文与附件。密码使用当前 Windows 用户 DPAPI 保护。","Encrypt message content and attachments between your device and Bot. Password is protected with Windows DPAPI."},{"连接现有 OpenCode，或安装独立的本地引擎。Bot 分叉本身不依赖上游 npm 包。","Connect to an existing OpenCode server or install a local engine."},{"设置 Windows 登录启动，并使用 opencode-telegram 命令管理 Bot。","Configure startup at Windows sign-in and manage the Bot with opencode-telegram."},{"先绑定 Telegram Bot，再设置 OpenCode 地址和 EOT10 密码。所有凭据保存在当前 Windows 用户的 DPAPI 加密设置中。","First connect your Telegram Bot, then configure OpenCode and EOT10. Credentials are encrypted with Windows DPAPI for the current user."},{"在窗口右上角切换简体中文或 English，选择后立即保存并应用。","Choose Simplified Chinese or English in the top-right menu. The change is saved and applied immediately."},{"当前语言","Current language"},{"使用窗口右上角的语言菜单切换。设置会保存在当前 Windows 用户的加密配置中。","Use the language menu in the top-right corner. Your choice is saved in your encrypted Windows user settings."},{"更改后点击保存。","Save your changes."},{"开机自启动选项已保存。","Startup options saved."},{"开机自启动已关闭。","Startup options are disabled."}};
        var extra=new Dictionary<string,string>{{"OpenCode 网页端","OpenCode Web"},{"OpenCode CLI","OpenCode CLI"},{"opencode_telegram_bot CLI","opencode_telegram_bot CLI"},{"输入 OpenCode 命令参数并查看实时输出；命令使用上方所选的 API 版本。","Enter OpenCode command arguments and view output. Commands use the API version selected above."},{"例如：--version 或 models","Example: --version or models"},{"运行命令","Run command"},{"运行桌面端 CLI","Run desktop CLI"},{"桌面端管理命令（例如 help、status、start、config、opencode 或 web），并查看输出。","Run desktop app commands (for example help, status, start, config, opencode, or web) and view their output."},{"设置登录启动，并运行桌面端自身的管理命令；同一 CLI 也可供脚本和 AI 调用。","Configure sign-in startup and run this desktop app's management commands. The same CLI is available to scripts and AI agents."},{"例如：status、config set model.id big-pickle 或 web status","Example: status, config set model.id big-pickle, or web status"},{"在默认浏览器打开","Open in default browser"},{"无法打开默认浏览器：","Unable to open browser: "},{"所选版本的 OpenCode CLI 未安装，请先到 OpenCode 引擎页面安装或扫描。","The selected OpenCode CLI is not installed. Install or scan for it on the OpenCode Engine page."},{"退出代码：","Exit code: "},{"命令执行失败：","Command failed: "},{"设置 Windows 登录启动，并在此直接运行所选版本的 OpenCode CLI 命令。","Configure Windows sign-in startup and run commands for the selected OpenCode CLI version here."},{"启动当前所选 API 版本对应的 OpenCode 网页服务，并在此页面内打开。","Start the web service for the selected API version and open it here."},{"当前版本：","Selected version: "},{"启动网页服务","Start Web Service"},{"刷新网页","Refresh page"},{"停止网页服务","Stop Web Service"},{"网页服务尚未启动。","Web service is not running."},{"所选版本的 CLI 尚未安装。请先到 OpenCode 引擎页面安装或扫描。","The selected CLI is not installed. Install or scan for it on the OpenCode Engine page."},{"正在启动网页服务…","Starting web service…"},{"网页服务运行中：","Web service running: "},{"服务仍在启动；可稍后点击网页区域刷新。","The service is still starting. Refresh the embedded page shortly."},{"网页服务启动失败，请检查所选版本和端口。","Web service failed to start. Check the selected version and port."},{"网页服务已停止。","Web service stopped."},{"无法启动网页服务：","Unable to start web service: "},{"停止网页服务失败：","Unable to stop web service: "}};
        if(extra.ContainsKey(text))return extra[text];
        return words.ContainsKey(text)?words[text]:text;
    }
    TextBlock Label(string text,double size,bool bold,Color color) { return new TextBlock { Text=Ui(text), FontFamily=new FontFamily("Segoe UI"), FontSize=size, FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal, Foreground=new SolidColorBrush(color), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,6) }; }
    Button ActionButton(string text,bool primary) { return new Button { Content=Ui(text), Padding=new Thickness(18,10,18,10), Margin=new Thickness(0,0,10,0), BorderThickness=new Thickness(0), Background=primary?new SolidColorBrush(Color.FromRgb(43,103,230)):new SolidColorBrush(Color.FromRgb(238,240,245)), Foreground=primary?Brushes.White:new SolidColorBrush(Color.FromRgb(52,60,78)), FontWeight=FontWeights.SemiBold, Cursor=Cursors.Hand }; }
    Button NavButton(string text,bool selected) { var b=new Button { Content=text, HorizontalContentAlignment=HorizontalAlignment.Left, Margin=new Thickness(12,3,12,3), Padding=new Thickness(15,11,10,11), BorderThickness=new Thickness(0), FontSize=14, FontWeight=FontWeights.Medium, Cursor=Cursors.Hand }; SetNavStyle(b,selected); return b; }
    void SetNavStyle(Button b,bool selected) { b.Background=selected?new SolidColorBrush(Color.FromRgb(224,232,251)):Brushes.Transparent; b.Foreground=selected?new SolidColorBrush(Color.FromRgb(38,91,202)):new SolidColorBrush(Color.FromRgb(90,98,119)); }
    bool SaveSettings(bool showStatus)
    {
        if (tokenBox != null) settings.BotToken=tokenBox.Password.Trim();
        if (userIdBox != null) settings.AllowedUserId=userIdBox.Text.Trim();
        if (eotEnabledBox != null) settings.Eot10Enabled=eotEnabledBox.IsChecked==true;
        if (eotPasswordBox != null && !String.IsNullOrEmpty(eotPasswordBox.Password)) settings.Eot10Password=eotPasswordBox.Password;
        if (urlBox != null) settings.OpenCodeUrl=urlBox.Text.Trim();
        if (serverVersionBox != null) settings.OpenCodeServerVersion=serverVersionBox.SelectedItem as string??"v1";
        if (modelProviderBox != null) settings.ModelProvider=modelProviderBox.Text.Trim();
        if (modelIdBox != null) settings.ModelId=modelIdBox.Text.Trim();
        if (openCodePathBox != null) settings.OpenCodeExecutable=openCodePathBox.Text.Trim();
        if (serverPasswordBox != null) settings.OpenCodePassword=serverPasswordBox.Password;
        if (settings.Eot10Enabled && (settings.Eot10Password??"").Length<12) { MessageBox.Show("EOT10 密码至少需要 12 个字符。", ProductPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        try { SettingsStore.Save(settings); ConfigureUserEnvironment(settings); if (showStatus) { SetStatus("设置已使用 Windows DPAPI 加密保存。",false); MessageBox.Show(Ui("设置已成功保存。"),ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Information); } return true; }
        catch(Exception ex) { SetStatus("设置保存失败："+ex.Message,true); MessageBox.Show("设置保存失败：\n"+ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error); return false; }
    }
    async Task RunBotAction(string action,bool saveFirst)
    {
        if(serviceActionRunning)return;
        serviceActionRunning=true;
        if(serviceToggle!=null){serviceToggle.IsEnabled=false;serviceToggle.Content=settings.Locale=="en"?"Applying…":"正在应用中…";}
        SetStatus(settings.Locale=="en"?"Applying service changes…":"正在应用服务设置，请稍候…",false);
        try
        {
            if(saveFirst && !SaveSettings(false)) return;
            if(action=="start" && (String.IsNullOrWhiteSpace(settings.BotToken) || String.IsNullOrWhiteSpace(settings.AllowedUserId))) throw new InvalidOperationException("请先在“机器人账号”页面填写 Bot Token 和允许的用户 ID。");
            SetStatus(settings.Locale=="en"?"Applying service changes…":"正在应用服务设置，请稍候…",false);
            string result=await Task.Run(()=>action=="start"?BotRuntime.Start(settings,message=>window.Dispatcher.BeginInvoke(new Action(()=>SetStatus(message,false)))):BotRuntime.Execute(action,settings,60));
            if(action=="start"&&serverPasswordBox!=null)serverPasswordBox.Password=settings.OpenCodePassword??"";
            if(action=="start"&&urlBox!=null)urlBox.Text=BotRuntime.ResolveOpenCodeUrl(settings);
            SetStatus(String.IsNullOrWhiteSpace(result)?"命令已完成。":result,false);
            serviceActionRunning=false;
            await RefreshStatus();
        }
        catch(Exception ex)
        {
            SetStatus("操作失败："+ex.Message,true);
            if(action=="start"&&ex is OpenCodeInstallRequiredException)
            {
                MessageBox.Show(ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Warning);
                ShowPage(3);
                SetOpenCodeFeedback(ex.Message,true);
            }
        }
        finally
        {
            serviceActionRunning=false;
            if(serviceToggle!=null){serviceToggle.IsEnabled=true;serviceToggle.Content=Ui(serviceToggle.Tag as string=="stop"?"停止服务":"启动服务");}
        }
    }
    bool serviceActionRunning;
    async Task RefreshStatus()
    {
        if(statusRefreshRunning||serviceActionRunning)return;
        statusRefreshRunning=true;
        try
        {
            string serviceStatus=await Task.Run(()=>BotRuntime.Status(settings));
            if(serviceActionRunning)return;
            // Keep the last visible status while polling or awaiting a usable result.
            if(String.IsNullOrWhiteSpace(serviceStatus))return;
            SetStatus(serviceStatus,false);
            bool running=serviceStatus.IndexOf("Service status: running",StringComparison.OrdinalIgnoreCase)>=0;
            if(serviceToggle!=null){serviceToggle.Content=Ui(running?"停止服务":"启动服务");serviceToggle.Tag=running?"stop":"start";serviceToggle.Background=running?new SolidColorBrush(Color.FromRgb(180,65,65)):new SolidColorBrush(Color.FromRgb(43,103,230));}
            activeServiceLogPath=running?GetStatusValue(serviceStatus,"Log file:"):null;
            LoadLog();
        }
        catch(Exception ex)
        {
            if(serviceActionRunning)return;
            activeServiceLogPath=null;
            SetStatus("状态读取失败："+ex.Message,true);
            LoadLog();
        }
        finally { statusRefreshRunning=false; }
    }
    static string GetStatusValue(string status,string prefix)
    {
        foreach(string line in status.Split(new[]{"\r\n","\n"},StringSplitOptions.None))
            if(line.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) return line.Substring(prefix.Length).Trim();
        return null;
    }
    void LoadLog()
    {
        if(logView==null)return;
        if(!logView.Dispatcher.CheckAccess()) { logView.Dispatcher.BeginInvoke(new Action(LoadLog)); return; }
        if(String.IsNullOrWhiteSpace(activeServiceLogPath)){logView.Text="Logs will appear here after the service starts.";return;}
        try
        {
            if(!File.Exists(activeServiceLogPath)){logView.Text="Waiting for the service to write its first log entry…";return;}
            var tail=new Queue<string>();
            using(var stream=new FileStream(activeServiceLogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new StreamReader(stream))
            {
                string line;
                while((line=reader.ReadLine())!=null){if(tail.Count==250)tail.Dequeue();tail.Enqueue(line);}
            }
            string logText=tail.Count==0?"Waiting for the service to write its first log entry…":String.Join(Environment.NewLine,tail);
            if(logView.Text!=logText)
            {
                bool followTail=logView.VerticalOffset>=logView.ExtentHeight-logView.ViewportHeight-1;
                double previousOffset=logView.VerticalOffset;
                logView.Text=logText;
                if(followTail)logView.ScrollToEnd();
                else logView.ScrollToVerticalOffset(previousOffset);
            }
        }
        catch(Exception ex){logView.Text="Unable to read the runtime log ("+ex.GetType().Name+").";}
    }
    async Task InstallOpenCode(string apiVersion)
    {
        if(serverVersionBox!=null) serverVersionBox.SelectedItem=apiVersion;
        if(urlBox!=null) urlBox.Text=apiVersion=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096";
        if(apiVersion=="v2")
        {
            settings.OpenCodePassword="";
            if(serverPasswordBox!=null) serverPasswordBox.Clear();
        }
        if (!SaveSettings(false)) return;
        openCodeOperationRunning=true;
        UpdateOpenCodeEngineButtons();
        SetOpenCodeFeedback(settings.Locale=="en"?"Checking Node.js and Bun dependencies, then installing OpenCode API "+apiVersion+". Keep the app open.":"正在检测 Node.js / Bun 依赖并安装 OpenCode API "+apiVersion+"，请保持应用打开。",false);
        try
        {
            settings.OpenCodeExecutable=await Task.Run(()=>BotRuntime.InstallOpenCode(settings,apiVersion,message=>window.Dispatcher.BeginInvoke(new Action(()=>SetOpenCodeFeedback(message,false)))));
            settings.OpenCodeServerVersion=apiVersion;
            settings.OpenCodeUrl=apiVersion=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096";
            await Task.Run(()=>BotRuntime.ConfigureSelectedV2Installation(settings,false));
            if(serverPasswordBox!=null)serverPasswordBox.Password=settings.OpenCodePassword??"";
            if(urlBox!=null) urlBox.Text=settings.OpenCodeUrl;
            if(openCodePathBox!=null) openCodePathBox.Text=settings.OpenCodeExecutable;
            SettingsStore.Save(settings);
            string installedVersion=await Task.Run(()=>BotRuntime.GetInstalledOpenCodeVersion(settings));
            SetOpenCodeFeedback(settings.Locale=="en"?"OpenCode API "+apiVersion+" installed successfully ("+installedVersion+"). URL: "+settings.OpenCodeUrl:"OpenCode API "+apiVersion+" 安装成功（"+installedVersion+"）。服务地址："+settings.OpenCodeUrl,false);
            MessageBox.Show(settings.Locale=="en"?"OpenCode API "+apiVersion+" was installed successfully.\nVersion: "+installedVersion+"\nURL: "+settings.OpenCodeUrl:"OpenCode API "+apiVersion+" 已安装成功。\n版本："+installedVersion+"\n地址："+settings.OpenCodeUrl,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            SetOpenCodeFeedback(settings.Locale=="en"?"OpenCode API "+apiVersion+" installation failed: "+ex.Message:"OpenCode API "+apiVersion+" 安装失败："+ex.Message,true);
            MessageBox.Show(settings.Locale=="en"?"OpenCode API "+apiVersion+" installation failed:\n"+ex.Message:"OpenCode API "+apiVersion+" 安装失败：\n"+ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            openCodeOperationRunning=false;
            UpdateOpenCodeEngineButtons();
        }
    }
    async Task UninstallOpenCode(string apiVersion)
    {
        string confirm=settings.Locale=="en"
            ? "Completely remove the app-managed OpenCode API "+apiVersion+" engine and stop its processes? Global OpenCode installations will not be changed."
            : "确定彻底删除桌面端管理的 OpenCode API "+apiVersion+" 引擎并停止其相关进程吗？不会改动全局安装的 OpenCode。";
        if(MessageBox.Show(confirm,ProductPaths.AppName,MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
        openCodeOperationRunning=true;
        UpdateOpenCodeEngineButtons();
        SetOpenCodeFeedback(settings.Locale=="en"?"Uninstalling OpenCode API "+apiVersion+"…":"正在卸载 OpenCode API "+apiVersion+"…",false);
        try
        {
            bool botStopped=false;
            if(settings.OpenCodeServerVersion==apiVersion&&BotRuntime.IsLocalOpenCodeUrl(BotRuntime.ResolveOpenCodeUrl(settings)))
            {
                string status=await Task.Run(()=>BotRuntime.Status(settings));
                if(status.IndexOf("Service status: running",StringComparison.OrdinalIgnoreCase)>=0)
                {
                    await Task.Run(()=>BotRuntime.Execute("stop",settings,30));
                    botStopped=true;
                }
            }
            await Task.Run(()=>BotRuntime.UninstallOpenCode(apiVersion));
            string engineRoot=Path.GetFullPath(Path.Combine(ProductPaths.Home,"opencode-runtime",apiVersion))+Path.DirectorySeparatorChar;
            if(!String.IsNullOrWhiteSpace(settings.OpenCodeExecutable))
            {
                try
                {
                    if(Path.GetFullPath(settings.OpenCodeExecutable).StartsWith(engineRoot,StringComparison.OrdinalIgnoreCase)) settings.OpenCodeExecutable="";
                }
                catch { }
            }
            if(openCodePathBox!=null) openCodePathBox.Text=settings.OpenCodeExecutable;
            SettingsStore.Save(settings);
            string message=settings.Locale=="en"?"OpenCode API "+apiVersion+" was uninstalled successfully."+(botStopped?" The Bot service was stopped because it was using this engine.":""):"OpenCode API "+apiVersion+" 已卸载成功。"+(botStopped?"该版本正被 Bot 使用，因此 Bot 服务也已停止。":"");
            SetOpenCodeFeedback(message,false);
            MessageBox.Show(message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            string message=settings.Locale=="en"?"OpenCode API "+apiVersion+" uninstall failed: "+ex.Message:"OpenCode API "+apiVersion+" 卸载失败："+ex.Message;
            SetOpenCodeFeedback(message,true);
            MessageBox.Show(message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            openCodeOperationRunning=false;
            UpdateOpenCodeEngineButtons();
        }
    }
    async Task ScanExistingOpenCode()
    {
        openCodeOperationRunning=true;
        UpdateOpenCodeEngineButtons();
        SetOpenCodeFeedback(settings.Locale=="en"?"Scanning process paths, PATH, npm/Bun globals, Windows install records, and the app's engine folders…":"正在扫描运行进程、各级 PATH、npm/Bun 全局目录、Windows 安装记录和应用内引擎目录…",false);
        try
        {
            var installations=await Task.Run(()=>BotRuntime.ScanOpenCodeInstallations(settings));
            if(installations.Count==0)
            {
                string message=settings.Locale=="en"?"No OpenCode 1.x or 2.x CLI was found. Install the desired API from this page.":"没有扫描到 OpenCode 1.x 或 2.x CLI。请在本页安装需要的 API 版本。";
                SetOpenCodeFeedback(message,true);
                MessageBox.Show(message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            OpenCodeInstallation installation=ChooseOpenCodeInstallation(installations);
            if(installation==null)
            {
                SetOpenCodeFeedback(settings.Locale=="en"?"OpenCode selection cancelled.":"已取消 OpenCode 选择。",false);
                return;
            }
            if(serverVersionBox!=null) serverVersionBox.SelectedItem=installation.ApiVersion;
            settings.OpenCodeServerVersion=installation.ApiVersion;
            settings.OpenCodeUrl=BotRuntime.GetDefaultApiUrl(installation.ApiVersion);
            settings.OpenCodeExecutable=installation.Executable;
            await Task.Run(()=>BotRuntime.ConfigureSelectedV2Installation(settings,true));
            if(serverPasswordBox!=null)serverPasswordBox.Password=settings.OpenCodePassword??"";
            if(urlBox!=null) urlBox.Text=settings.OpenCodeUrl;
            if(openCodePathBox!=null) openCodePathBox.Text=settings.OpenCodeExecutable;
            if(!SaveSettings(false)) return;
            string successMessage=settings.Locale=="en"
                ? "Using existing OpenCode "+installation.Version+" (API "+installation.ApiVersion+"). URL: "+settings.OpenCodeUrl
                : "已选择现有 OpenCode "+installation.Version+"（API "+installation.ApiVersion+"）。服务地址："+settings.OpenCodeUrl;
            SetOpenCodeFeedback(successMessage,false);
        }
        catch(Exception ex)
        {
            string errorMessage=settings.Locale=="en"?"OpenCode scan failed: "+ex.Message:"扫描 OpenCode 失败："+ex.Message;
            SetOpenCodeFeedback(errorMessage,true);
            MessageBox.Show(errorMessage,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            openCodeOperationRunning=false;
            UpdateOpenCodeEngineButtons();
        }
    }
    OpenCodeInstallation ChooseOpenCodeInstallation(List<OpenCodeInstallation> installations)
    {
        if(installations.Count==1) return installations[0];
        var dialog=new Window
        {
            Title=settings.Locale=="en"?"Select OpenCode":"选择 OpenCode",
            Width=720,
            Height=260,
            Owner=window,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            ResizeMode=ResizeMode.NoResize,
            WindowStyle=WindowStyle.SingleBorderWindow,
            Background=new SolidColorBrush(Color.FromRgb(247,248,252)),
        };
        var panel=new StackPanel{Margin=new Thickness(22)};
        panel.Children.Add(Label(settings.Locale=="en"?"Multiple OpenCode API versions were found. Choose which one this Bot should use.":"检测到多个 OpenCode API 版本，请选择 Bot 要使用的版本。",14,true,Color.FromRgb(39,46,65)));
        var choices=new ComboBox{Height=76,Margin=new Thickness(0,12,0,14),ItemsSource=installations,DisplayMemberPath="DisplayName"};
        choices.SelectedIndex=0;
        panel.Children.Add(choices);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var use=ActionButton(settings.Locale=="en"?"Use selected version":"使用此版本",true);
        use.Click+=(sender,args)=>{if(choices.SelectedItem!=null)dialog.DialogResult=true;};
        var cancel=ActionButton(settings.Locale=="en"?"Cancel":"取消",false);
        cancel.Click+=(sender,args)=>dialog.DialogResult=false;
        actions.Children.Add(use); actions.Children.Add(cancel); panel.Children.Add(actions);
        dialog.Content=panel;
        return dialog.ShowDialog()==true?choices.SelectedItem as OpenCodeInstallation:null;
    }
    void UpdateOpenCodeEngineButtons()
    {
        if(installV1Button==null) return;
        installV1Button.IsEnabled=!openCodeOperationRunning;
        installV2Button.IsEnabled=!openCodeOperationRunning;
        uninstallV1Button.IsEnabled=!openCodeOperationRunning&&Directory.Exists(Path.Combine(ProductPaths.Home,"opencode-runtime","v1"));
        uninstallV2Button.IsEnabled=!openCodeOperationRunning&&Directory.Exists(Path.Combine(ProductPaths.Home,"opencode-runtime","v2"));
        scanOpenCodeButton.IsEnabled=!openCodeOperationRunning;
    }
    void SetOpenCodeFeedback(string text,bool error)
    {
        if(openCodeFeedback==null) return;
        if(!openCodeFeedback.Dispatcher.CheckAccess())
        {
            openCodeFeedback.Dispatcher.BeginInvoke(new Action(()=>SetOpenCodeFeedback(text,error)));
            return;
        }
        openCodeFeedback.Text=text;
        openCodeFeedback.Foreground=new SolidColorBrush(error?Color.FromRgb(191,55,55):Color.FromRgb(39,46,65));
    }
    void SetStatus(string text,bool error)
    {
        if (statusText == null) return;
        if (!statusText.Dispatcher.CheckAccess())
        {
            statusText.Dispatcher.BeginInvoke(new Action(() => SetStatus(text, error)));
            return;
        }
        string displayText=Ui(text);
        if(statusText.Text!=displayText)statusText.Text=displayText;
        Color color=error?Color.FromRgb(191,55,55):Color.FromRgb(39,46,65);
        var brush=statusText.Foreground as SolidColorBrush;
        if(brush==null||brush.Color!=color)statusText.Foreground=new SolidColorBrush(color);
    }

    static void ConfigureUserEnvironment(DesktopSettings s)
    {
        string root=ProductPaths.Root, home=ProductPaths.Home;
        Environment.SetEnvironmentVariable(ProductPaths.EnvRoot,root,EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(ProductPaths.EnvHome,home,EnvironmentVariableTarget.User);
        string old=Environment.GetEnvironmentVariable("PATH",EnvironmentVariableTarget.User)??"";
        var items=old.Split(Path.PathSeparator).Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();
        string bin=Path.Combine(root,"bin");
        items.RemoveAll(x=>String.Equals(x.TrimEnd('\\','/'),bin.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase));
        items.Insert(0,bin);
        Environment.SetEnvironmentVariable("PATH",String.Join(Path.PathSeparator.ToString(),items),EnvironmentVariableTarget.User);
        try { using(var key=Registry.CurrentUser.OpenSubKey("Environment",true)) { SendMessageTimeout(new IntPtr(0xffff),0x001A,IntPtr.Zero,"Environment",0x0002,5000,IntPtr.Zero); } } catch { }
    }
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Auto)] static extern IntPtr SendMessageTimeout(IntPtr hWnd,uint msg,IntPtr wParam,string lParam,uint flags,uint timeout,IntPtr result);
}

sealed class InstallerWindow : Window
{
    TextBox target;
    TextBlock status;
    StackPanel installerStack;
    CheckBox launchAfterInstall;
    string installedExe;
    bool english;
    public InstallerWindow()
    {
        english=SettingsStore.Load().Locale=="en";
        Width=650; Height=490; WindowStartupLocation=WindowStartupLocation.CenterScreen; WindowStyle=WindowStyle.SingleBorderWindow; Background=new SolidColorBrush(Color.FromRgb(247,248,252)); ResizeMode=ResizeMode.NoResize; Title=ProductPaths.AppName;
        var border=new Border{CornerRadius=new CornerRadius(24),Background=new SolidColorBrush(Color.FromRgb(247,248,252)),Padding=new Thickness(34),BorderBrush=new SolidColorBrush(Color.FromRgb(226,229,237)),BorderThickness=new Thickness(1),Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=32,ShadowDepth=5,Opacity=.15}};
        installerStack=new StackPanel(); border.Child=installerStack; Content=border;
        installerStack.Children.Add(new TextBlock{Text=ProductPaths.AppName,FontSize=29,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(34,40,56)),Margin=new Thickness(0,4,0,8)});
        installerStack.Children.Add(new TextBlock{Text=english?"OpenCode installer and launcher · Telegram remote control · encrypted communication":"OpenCode 安装与启动器 · Telegram 远程控制 · 加密通信",FontSize=14,Foreground=new SolidColorBrush(Color.FromRgb(108,116,137)),Margin=new Thickness(0,0,0,24),TextWrapping=TextWrapping.Wrap});
        installerStack.Children.Add(new TextBlock{Text=english?"Install location":"安装位置",FontSize=13,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,6)});
        target=new TextBox{Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","OpenCodeTelegram"),Height=40,Padding=new Thickness(12,8,12,8),FontSize=14};
        var pathRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,16)}; target.Margin=new Thickness(0,0,8,0); target.Width=470; pathRow.Children.Add(target);
        var browse=new Button{Content=english?"Browse…":"浏览…",Width=88,Height=40}; browse.Click+=(s,e)=>{using(var dialog=new Forms.FolderBrowserDialog()){dialog.Description=english?"Choose the installation folder":"选择安装文件夹";dialog.SelectedPath=target.Text;if(dialog.ShowDialog()==Forms.DialogResult.OK)target.Text=dialog.SelectedPath;}}; pathRow.Children.Add(browse); installerStack.Children.Add(pathRow);
        status=new TextBlock{Text=english?"The setup installs the app and creates Start menu and desktop shortcuts. You can choose whether to launch it when setup finishes.":"安装程序会安装应用并创建开始菜单和桌面快捷方式。安装结束时可选择是否立即启动。",FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(112,120,140)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,22)}; installerStack.Children.Add(status);
        var button=new Button{Content=english?"Install":"安装",Height=46,FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(43,103,230)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand}; button.Click+=async(s,e)=>await Install(); installerStack.Children.Add(button);
    }
    async Task Install()
    {
        string dest=target.Text.Trim();
        if(String.IsNullOrWhiteSpace(dest)){status.Text=english?"Choose an installation folder.":"请选择安装目录。";return;}
        try
        {
            bool enableAppStartup=AppStartupSetting.IsEnabled();
            bool enableBotStartup=StartupSetting.IsEnabled();
            bool wasBotRunning=false;
            var currentSettings=SettingsStore.Load();
            try { wasBotRunning=BotRuntime.Status(currentSettings).IndexOf("Service status: running",StringComparison.OrdinalIgnoreCase)>=0; } catch { }
            status.Text=english?"Stopping the running Bot…":"正在停止已运行的 Bot…";
            if(wasBotRunning) { try { BotRuntime.Execute("stop",currentSettings,20); } catch { } }
            Environment.SetEnvironmentVariable(ProductPaths.EnvRoot,dest);
            foreach(var running in Process.GetProcessesByName("OpenCodeTelegram")) { try { if(String.Equals(running.MainModule.FileName,Path.Combine(dest,"OpenCodeTelegram.exe"),StringComparison.OrdinalIgnoreCase)) running.Kill(); } catch{} }
            await Task.Delay(700);
            status.Text=english?"Extracting the standalone runtime…":"正在解压独立运行时…";
            await Task.Run(()=>{
                Directory.CreateDirectory(dest);
                Stream runtimeArchive=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("BotRuntime.zip");
                if(runtimeArchive==null)
                {
                    string[] candidates={Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Dependencies","BotRuntime.zip"),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"BotRuntime.zip")};
                    string external=candidates.FirstOrDefault(File.Exists);
                    if(external!=null) runtimeArchive=File.OpenRead(external);
                }
                using(Stream resource=runtimeArchive)
                {
                    if(resource==null) throw new InvalidOperationException("安装包未包含 BotRuntime.zip。");
                    using(var archive=new ZipArchive(resource,ZipArchiveMode.Read))
                    foreach(var entry in archive.Entries)
                    {
                        string output=Path.GetFullPath(Path.Combine(dest,entry.FullName));
                        if(!output.StartsWith(Path.GetFullPath(dest)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe installer entry.");
                        if(String.IsNullOrEmpty(entry.Name)){Directory.CreateDirectory(output);continue;}
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        entry.ExtractToFile(output,true);
                    }
                }
                installedExe=Path.Combine(dest,"OpenCodeTelegram.exe");
                if(!File.Exists(installedExe)) throw new FileNotFoundException("Installer payload is missing the desktop app executable.",installedExe);
                string uninstaller=Path.Combine(dest,"Uninstall.exe");
                File.Copy(installedExe,uninstaller,true);
                string bin=Path.Combine(dest,"bin");Directory.CreateDirectory(bin);
                File.WriteAllText(Path.Combine(bin,"opencode-telegram.cmd"),"@echo off\r\n\"%~dp0..\\OpenCodeTelegram.exe\" %*\r\n",new UTF8Encoding(false));
                Environment.SetEnvironmentVariable(ProductPaths.EnvRoot,dest,EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable(ProductPaths.EnvHome,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"OpenCodeTelegram"),EnvironmentVariableTarget.User);
                string userPath=Environment.GetEnvironmentVariable("PATH",EnvironmentVariableTarget.User)??"";
                var paths=userPath.Split(Path.PathSeparator).Where(p=>!String.IsNullOrWhiteSpace(p)).ToList();
                paths.RemoveAll(p=>String.Equals(p.TrimEnd('\\','/'),bin.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase));
                paths.Insert(0,bin);
                Environment.SetEnvironmentVariable("PATH",String.Join(Path.PathSeparator.ToString(),paths),EnvironmentVariableTarget.User);
                DesktopAppBroadcast.EnvironmentChanged();
                AppStartupSetting.Set(enableAppStartup);
                StartupSetting.Set(enableBotStartup);
                string programs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs");
                string oldPrograms=Path.Combine(programs,"OpenCode Telegram");
                string oldStartLink=Path.Combine(oldPrograms,"OpenCode Telegram.lnk");
                if(File.Exists(oldStartLink)) File.Delete(oldStartLink);
                if(Directory.Exists(oldPrograms)&&Directory.GetFileSystemEntries(oldPrograms).Length==0) Directory.Delete(oldPrograms);
                string oldDesktopLink=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"OpenCode Telegram.lnk");
                if(File.Exists(oldDesktopLink)) File.Delete(oldDesktopLink);
                CreateShortcut(Path.Combine(programs,ProductPaths.AppName,ProductPaths.AppName+".lnk"),installedExe,dest);
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),ProductPaths.AppName+".lnk"),installedExe,dest);
                ProductRegistration.Register(dest);
            });
            if(wasBotRunning) { status.Text=english?"Installation complete. Restarting the Bot service…":"安装完成，正在恢复 Bot 服务…"; await Task.Run(()=>BotRuntime.Start(SettingsStore.Load(),message=>Dispatcher.BeginInvoke(new Action(()=>status.Text=message)))); }
            ShowCompletionPage();
        }
        catch(Exception ex){status.Text=(english?"Installation failed: ":"安装失败：")+ex.Message;}
    }
    void ShowCompletionPage()
    {
        installerStack.Children.Clear();
        installerStack.Children.Add(new TextBlock{Text=english?"Installation complete":"安装完成",FontSize=27,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(34,40,56)),Margin=new Thickness(0,4,0,12)});
        installerStack.Children.Add(new TextBlock{Text=english?"opencode_telegram_bot has been installed. Use the desktop shortcut to open it later.":"opencode_telegram_bot 已安装完成。之后可通过桌面快捷方式打开程序。",FontSize=14,Foreground=new SolidColorBrush(Color.FromRgb(108,116,137)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,22)});
        launchAfterInstall=new CheckBox{Content=english?"Launch opencode_telegram_bot now":"立即启动 opencode_telegram_bot",IsChecked=false,FontSize=14,Margin=new Thickness(0,0,0,24)};
        installerStack.Children.Add(launchAfterInstall);
        var finish=new Button{Content=english?"Finish":"完成",Height=46,FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(43,103,230)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        finish.Click+=(s,e)=>
        {
            try { if(launchAfterInstall.IsChecked==true) Process.Start(new ProcessStartInfo{FileName=installedExe,UseShellExecute=true}); Close(); }
            catch(Exception ex) { status.Text=(english?"Unable to launch the app: ":"无法启动程序：")+ex.Message; installerStack.Children.Add(status); }
        };
        installerStack.Children.Add(finish);
    }
    static void CreateShortcut(string shortcutPath,string targetPath,string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
        Type shellType=Type.GetTypeFromProgID("WScript.Shell");
        object shell=Activator.CreateInstance(shellType);
        object shortcut=shellType.InvokeMember("CreateShortcut",System.Reflection.BindingFlags.InvokeMethod,null,shell,new object[]{shortcutPath});
        Type shortcutType=shortcut.GetType();
        shortcutType.InvokeMember("TargetPath",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{targetPath});
        shortcutType.InvokeMember("WorkingDirectory",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{workingDirectory});
        shortcutType.InvokeMember("Description",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{ProductPaths.AppName+" - OpenCode installer and Telegram remote-control launcher"});
        shortcutType.InvokeMember("Save",System.Reflection.BindingFlags.InvokeMethod,null,shortcut,null);
    }
}

static class DesktopAppBroadcast
{
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Auto)] static extern IntPtr SendMessageTimeout(IntPtr hWnd,uint msg,IntPtr wParam,string lParam,uint flags,uint timeout,IntPtr result);
    public static void EnvironmentChanged(){try{SendMessageTimeout(new IntPtr(0xffff),0x001A,IntPtr.Zero,"Environment",0x0002,5000,IntPtr.Zero);}catch{}}
}

static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint processId);
    const uint ATTACH_PARENT_PROCESS = 0xffffffff;
    [STAThread]
    static int Main(string[] args)
    {
        if(args!=null&&args.Length>0&&args[0]=="--embedded-cli")
        {
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            Console.SetError(new StreamWriter(Console.OpenStandardError()){AutoFlush=true});
            try{return Cli.Run(args.Skip(1).ToArray());}catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
        }
        if(args != null && args.Length > 0 && (args[0]=="help"||args[0]=="--help"||args[0]=="-h"||args[0]=="version"||args[0]=="--version"||args[0]=="status"||args[0]=="start"||args[0]=="stop"||args[0]=="autostart"||args[0]=="environment"||args[0]=="configure"||args[0]=="config"||args[0]=="opencode"||args[0]=="app-autostart"||args[0]=="language"||args[0]=="logs"||args[0]=="web"||args[0]=="--uninstall"))
        {
            if(AttachConsole(ATTACH_PARENT_PROCESS))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush=true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush=true });
            }
            return Cli.Run(args);
        }
        if(args != null && args.Length > 0 && args[0]=="--start-silent") { try { BotRuntime.Start(SettingsStore.Load(),message=>{try{Directory.CreateDirectory(ProductPaths.Home);File.AppendAllText(Path.Combine(ProductPaths.Home,"startup.log"),DateTime.Now.ToString("s")+" "+message+Environment.NewLine);}catch{}}); return 0; } catch(Exception ex){File.AppendAllText(Path.Combine(ProductPaths.Home,"startup-error.log"),DateTime.Now.ToString("s")+" "+ex+Environment.NewLine);return 1;} }
#if SETUP_BUILD
        var setupApp=new Application(); setupApp.Run(new InstallerWindow()); return 0;
#else
        try { ProductRegistration.Register(ProductPaths.Root); } catch { }
        new DesktopApp().RunGui();
        return 0;
#endif
    }
}
