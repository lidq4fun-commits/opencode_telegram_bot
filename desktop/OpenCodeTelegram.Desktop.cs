using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
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
    static string ResolveOpenCodeExecutable(DesktopSettings settings)
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
    static string BuildOpenCodePath(DesktopSettings settings,string executableOverride=null)
    {
        string executable=String.IsNullOrWhiteSpace(executableOverride)?ResolveOpenCodeExecutable(settings):executableOverride;
        string executableDirectory=String.IsNullOrWhiteSpace(executable)?"":Path.GetDirectoryName(executable);
        return ProductPaths.BotModulesBin+Path.PathSeparator+executableDirectory+Path.PathSeparator+(Environment.GetEnvironmentVariable("PATH")??"");
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
    static string EnsureOpenCodeDependencies(string apiVersion)
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
            RunWingetInstall("Oven-sh.Bun");
            if(String.IsNullOrWhiteSpace(ResolveBunExecutable())) throw new InvalidOperationException("Bun was installed but is not available yet. Restart the desktop app and retry.");
        }
        return node;
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
    public static string Start(DesktopSettings settings)
    {
        EnsureOpenCodeInstalled(settings);
        var stopped = StopCompetingBotProcesses();
        var stoppedOpenCode = StopOpenCodeProcesses();
        string result = Execute("start --daemon --mode installed", settings, 60);
        if(stopped.Count>0) result="Stopped conflicting Bot process(es): "+String.Join(", ",stopped)+Environment.NewLine+result;
        if(stoppedOpenCode.Count>0) result="Stopped OpenCode process(es): "+String.Join(", ",stoppedOpenCode)+Environment.NewLine+result;
        return result;
    }
    static List<int> StopOpenCodeProcesses()
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
    static List<int> StopCompetingBotProcesses()
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
    public static string InstallOpenCode(DesktopSettings settings, string apiVersion)
    {
        if(apiVersion!="v1"&&apiVersion!="v2") throw new ArgumentException("OpenCode API version must be v1 or v2.","apiVersion");
        Directory.CreateDirectory(ProductPaths.Home);
        string node=EnsureOpenCodeDependencies(apiVersion);
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
    const string Usage = "opencode_telegram_bot\n\n" +
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
      "  opencode install            Install a local OpenCode engine\n" +
      "  autostart enable|disable    Control Windows sign-in startup\n" +
      "  autostart status            Show startup setting\n" +
      "  uninstall                   Uninstall this desktop application\n" +
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
        if (args[0] == "opencode" && args.Length > 1 && args[1] == "install") return InstallOpenCode();
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
            else if (args[0] == "start") Console.WriteLine(BotRuntime.Start(s));
            else if (args[0] == "stop") Console.WriteLine(BotRuntime.Stop(s));
            else { Console.Error.WriteLine(Usage); return 2; }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
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
            Console.WriteLine("Model: " + (settings.ModelProvider ?? "opencode") + "/" + (settings.ModelId ?? "big-pickle"));
            Console.WriteLine("Autostart: " + (StartupSetting.IsEnabled() ? "enabled" : "disabled"));
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
        return String.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    static string ReadSecret(string prompt, bool allowKeepCurrent = false, string current = "")
    {
        Console.Write(prompt);
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

    static int InstallOpenCode()
    {
        var settings = SettingsStore.Load();
        try
        {
            settings.OpenCodeExecutable = BotRuntime.InstallOpenCode(settings,settings.OpenCodeServerVersion??"v1");
            settings.OpenCodeUrl = settings.OpenCodeServerVersion == "v2" ? "http://127.0.0.1:49374" : "http://127.0.0.1:4096";
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

    public void RunGui()
    {
        settings = SettingsStore.Load();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        window = CreateWindow();
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
        navLabels = new string[] { "概览", "机器人账号", "加密设置", "OpenCode 引擎", "启动与 CLI", "语言与外观" };
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
        content.Children.Clear();
        if (activeNav != null) SetNavStyle(activeNav, false);
        foreach (var child in sidebar.Children) if (child is Button) SetNavStyle((Button)child, false);
        activeNav = sidebar.Children.OfType<Button>().ElementAt(page); SetNavStyle(activeNav, true);
        if (page == 0) { BuildDashboard(); logTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)}; logTimer.Tick+=async(s,e)=>await RefreshStatus(); logTimer.Start(); } else if (page == 1) BuildBotSettings(); else if (page == 2) BuildEncryptionSettings(); else if (page == 3) BuildOpenCodeSettings(); else if(page==4) BuildStartupSettings(); else BuildLanguageSettings();
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
        advancedContent.Children.Add(Label("Server 密码（可选；仅启用身份验证时填写）",12,true,Color.FromRgb(83,91,111))); serverPasswordBox=new PasswordBox { Password=settings.OpenCodePassword, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; advancedContent.Children.Add(serverPasswordBox);
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
        AddHeading("启动与命令行", "设置 Windows 登录启动，并使用 opencode-telegram 命令管理 Bot。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        autoStartAppBox=new CheckBox { Content=Ui("登录 Windows 时启动桌面软件"), IsChecked=AppStartupSetting.IsEnabled(), FontSize=15, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,2,0,14) }; s.Children.Add(autoStartAppBox);
        autoStartBox=new CheckBox { Content=Ui("登录 Windows 时自动启动 Bot"), IsChecked=StartupSetting.IsEnabled(), FontSize=15, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,2,0,14) }; s.Children.Add(autoStartBox);
        var save=ActionButton("保存启动选项",true); save.Click+=(a,b)=>{ try { AppStartupSetting.Set(autoStartAppBox.IsChecked==true); StartupSetting.Set(autoStartBox.IsChecked==true); settings.AutoStartApp=autoStartAppBox.IsChecked==true; settings.AutoStart=autoStartBox.IsChecked==true; SettingsStore.Save(settings); startupFeedback.Text=settings.AutoStartApp||settings.AutoStart?"开机自启动选项已保存。":"开机自启动已关闭。"; startupFeedback.Foreground=new SolidColorBrush(Color.FromRgb(33,128,74)); } catch(Exception ex) { startupFeedback.Text="保存开机自启动设置失败："+ex.Message; startupFeedback.Foreground=new SolidColorBrush(Color.FromRgb(191,55,55)); } }; s.Children.Add(save);
        startupFeedback=Label("更改后点击保存。",12,false,Color.FromRgb(105,113,133)); startupFeedback.Margin=new Thickness(0,12,0,4); s.Children.Add(startupFeedback);
        s.Children.Add(Label("命令行（PowerShell / CMD）",12,true,Color.FromRgb(83,91,111)));
        var cmd=Card(); var cs=new StackPanel(); cmd.Child=cs; cs.Children.Add(Label("opencode-telegram help\nopencode-telegram status\nopencode-telegram start\nopencode-telegram stop\nopencode-telegram autostart enable|disable|status",13,false,Color.FromRgb(59,67,87))); content.Children.Add(card); content.Children.Add(cmd);
    }

    void UpdateApiVersionHint()
    {
        if(apiVersionHint==null)return;
        bool v2=serverVersionBox!=null&&serverVersionBox.SelectedItem as string=="v2";
        apiVersionHint.Text=settings.Locale=="en"
            ? (v2?"V2 connects to an OpenCode 2.x server at this URL. A server password is optional and only needed if authentication is enabled. Install the V2 CLI below or connect to an existing V2 server.":"V1 connects to the classic OpenCode server (default port 4096).")
            : (v2?"V2 连接此地址上的 OpenCode 2.x 服务。服务密码为可选项，仅在服务启用身份验证时填写。可在下方安装 V2 CLI，或连接已有 V2 服务。":"V1 连接经典 OpenCode 服务（默认端口 4096）。");
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
        if(text=="OpenCode 安装与启动器") return "OpenCode installer and launcher";
        if(text=="OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。") return "An OpenCode installer and launcher with remote control via Telegram and encrypted communications.";
        if(text=="获取 Telegram Bot Token") return "Get a Telegram Bot Token";
        if(text=="1. 在 Telegram 搜索官方账号 @BotFather 并打开对话。\n2. 发送 /newbot，按提示设置 Bot 显示名称。\n3. 设置唯一用户名，必须以 bot 结尾（例如 my_opencode_bot）。\n4. BotFather 会返回一串 Token。复制后粘贴到下方 Bot Token 输入框；不要把 Token 发给任何人。") return "1. Search for the official @BotFather account on Telegram and open the chat.\n2. Send /newbot and follow the prompts to choose a display name.\n3. Choose a unique username ending in bot (for example, my_opencode_bot).\n4. Copy the token BotFather returns into the Bot Token field below. Never share this token.";
        if(text=="获取你自己的 Telegram 用户 ID") return "Get your Telegram user ID";
        if(text=="1. 在 Telegram 搜索 @userinfobot 并打开对话。\n2. 点击 Start 或发送 /start。\n3. 复制回复中的数字 ID，粘贴到下方允许的 Telegram User ID。\n请填写你个人账号的 ID，不是 Bot 的 ID。") return "1. Search for @userinfobot on Telegram and open the chat.\n2. Tap Start or send /start.\n3. Copy the numeric ID in its reply into Allowed Telegram User ID below.\nUse your personal account's ID, not the Bot's ID.";
        var words=new Dictionary<string,string>{{"概览","Overview"},{"机器人账号","Bot Account"},{"加密设置","Encryption"},{"OpenCode 引擎","OpenCode Engine"},{"启动与 CLI","Startup & CLI"},{"语言与外观","Language & Appearance"},{"控制中心","Control Center"},{"运行状态","Service Status"},{"启动机器人","Start Bot"},{"停止机器人","Stop Bot"},{"启动服务","Start service"},{"停止服务","Stop service"},{"刷新状态","Refresh Status"},{"快速开始","Quick Start"},{"保存机器人设置","Save Bot Settings"},{"保存加密设置","Save Encryption Settings"},{"登录 Windows 时自动启动 Bot","Start Bot at Windows sign-in"},{"登录 Windows 时启动桌面软件","Launch desktop app at Windows sign-in"},{"保存启动选项","Save Startup Settings"},{"安装 OpenCode","Install OpenCode"},{"安装 OpenCode（API v1）","Install OpenCode (API v1)"},{"安装 OpenCode（API v2）","Install OpenCode (API v2)"},{"保存引擎设置","Save Engine Settings"},{"运行日志","Runtime Log"},{"刷新日志","Refresh Log"},{"允许的 Telegram User ID","Allowed Telegram User ID"},{"加密密码（至少 12 个字符）","Encryption password (12+ characters)"},{"启用 EOT10 加密","Enable EOT10 encryption"},{"删除保存的密码并关闭 EOT10","Remove saved password and disable EOT10"},{"使用已安装 / 已运行的 OpenCode","Use existing OpenCode installation/server"},{"Server API 版本","Server API version"},{"Server API URL","Server API URL"},{"OpenCode 可执行文件（使用现有安装时可留空）","OpenCode executable (optional for existing installations)"},{"可选：OpenCode 可执行文件（通常无需手动填写，点击扫描自动识别）","Optional: OpenCode executable (usually detected automatically by scanning)"},{"扫描并选择已有 OpenCode","Scan and select existing OpenCode"},{"Server 用户名","Server username"},{"Server 密码","Server password"},{"Server 密码（可选；仅启用身份验证时填写）","Server password (optional; only if authentication is enabled)"},{"模型 Provider","Model provider"},{"模型 ID","Model ID"},{"EOT10 加密","EOT10 Encryption"},{"启动与命令行","Startup & Command Line"},{"独立桌面分叉","Standalone desktop app"},{"命令行（PowerShell / CMD）","Command line (PowerShell / CMD)"},{"设置已使用 Windows DPAPI 加密保存。","Settings saved securely with Windows DPAPI."},{"设置已成功保存。","Settings saved successfully."},{"开机自启动设置已保存。","Startup settings saved."},{"OpenCode 安装与启动器，可通过 Telegram 远程控制，并支持加密通信。","An OpenCode installer and launcher with remote control via Telegram and encrypted communications."},{"绑定你自己的 Telegram Bot，并限制允许操作的用户。","Connect your Telegram Bot and restrict which user can operate it."},{"获取 Telegram Bot Token","Get a Telegram Bot Token"},{"1. 在 Telegram 搜索官方账号 @BotFather 并打开对话。\n2. 发送 /newbot，按提示设置 Bot 显示名称。\n3. 设置唯一用户名，必须以 bot 结尾（例如 my_opencode_bot）。\n4. BotFather 会返回一串 Token。复制后粘贴到下方 Bot Token 输入框；不要把 Token 发给任何人。","1. Search for the official @BotFather account on Telegram and open the chat.\n2. Send /newbot and follow the prompts to choose a display name.\n3. Choose a unique username ending in bot (for example, my_opencode_bot).\n4. Copy the token BotFather returns into the Bot Token field below. Never share this token."},{"获取你自己的 Telegram 用户 ID","Get your Telegram user ID"},{"1. 在 Telegram 搜索 @userinfobot 并打开对话。\n2. 点击 Start 或发送 /start。\n3. 复制回复中的数字 ID，粘贴到下方允许的 Telegram User ID。\n请填写你个人账号的 ID，不是 Bot 的 ID。","1. Search for @userinfobot on Telegram and open the chat.\n2. Tap Start or send /start.\n3. Copy the numeric ID in its reply into Allowed Telegram User ID below.\nUse your personal account's ID, not the Bot's ID."},{"在设备端和 Bot 之间加密消息正文与附件。密码使用当前 Windows 用户 DPAPI 保护。","Encrypt message content and attachments between your device and Bot. Password is protected with Windows DPAPI."},{"连接现有 OpenCode，或安装独立的本地引擎。Bot 分叉本身不依赖上游 npm 包。","Connect to an existing OpenCode server or install a local engine."},{"设置 Windows 登录启动，并使用 opencode-telegram 命令管理 Bot。","Configure startup at Windows sign-in and manage the Bot with opencode-telegram."},{"先绑定 Telegram Bot，再设置 OpenCode 地址和 EOT10 密码。所有凭据保存在当前 Windows 用户的 DPAPI 加密设置中。","First connect your Telegram Bot, then configure OpenCode and EOT10. Credentials are encrypted with Windows DPAPI for the current user."},{"在窗口右上角切换简体中文或 English，选择后立即保存并应用。","Choose Simplified Chinese or English in the top-right menu. The change is saved and applied immediately."},{"当前语言","Current language"},{"使用窗口右上角的语言菜单切换。设置会保存在当前 Windows 用户的加密配置中。","Use the language menu in the top-right corner. Your choice is saved in your encrypted Windows user settings."},{"更改后点击保存。","Save your changes."},{"开机自启动选项已保存。","Startup options saved."},{"开机自启动已关闭。","Startup options are disabled."}};
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
        try
        {
            if(saveFirst && !SaveSettings(false)) return;
            if(action=="start" && (String.IsNullOrWhiteSpace(settings.BotToken) || String.IsNullOrWhiteSpace(settings.AllowedUserId))) throw new InvalidOperationException("请先在“机器人账号”页面填写 Bot Token 和允许的用户 ID。");
            SetStatus("正在执行 Bot 命令…",true);
            string result=await Task.Run(()=>action=="start"?BotRuntime.Start(settings):BotRuntime.Execute(action,settings,60));
            SetStatus(String.IsNullOrWhiteSpace(result)?"命令已完成。":result,false);
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
    }
    async Task RefreshStatus()
    {
        if(statusRefreshRunning)return;
        statusRefreshRunning=true;
        try
        {
            string serviceStatus=await Task.Run(()=>BotRuntime.Status(settings));
            SetStatus(serviceStatus,false);
            bool running=serviceStatus.IndexOf("Service status: running",StringComparison.OrdinalIgnoreCase)>=0;
            if(serviceToggle!=null){serviceToggle.Content=Ui(running?"停止服务":"启动服务");serviceToggle.Tag=running?"stop":"start";serviceToggle.Background=running?new SolidColorBrush(Color.FromRgb(180,65,65)):new SolidColorBrush(Color.FromRgb(43,103,230));}
            activeServiceLogPath=running?GetStatusValue(serviceStatus,"Log file:"):null;
            LoadLog();
        }
        catch(Exception ex)
        {
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
            logView.Text=tail.Count==0?"Waiting for the service to write its first log entry…":String.Join(Environment.NewLine,tail);
            logView.ScrollToEnd();
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
            settings.OpenCodeExecutable=await Task.Run(()=>BotRuntime.InstallOpenCode(settings,apiVersion));
            settings.OpenCodeServerVersion=apiVersion;
            settings.OpenCodeUrl=apiVersion=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096";
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
        statusText.Text = text;
        statusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(191, 55, 55) : Color.FromRgb(39, 46, 65));
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
            if(wasBotRunning) { status.Text=english?"Installation complete. Restarting the Bot service…":"安装完成，正在恢复 Bot 服务…"; await Task.Run(()=>BotRuntime.Start(SettingsStore.Load())); }
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
        if(args != null && args.Length > 0 && (args[0]=="help"||args[0]=="--help"||args[0]=="-h"||args[0]=="version"||args[0]=="--version"||args[0]=="status"||args[0]=="start"||args[0]=="stop"||args[0]=="autostart"||args[0]=="environment"||args[0]=="configure"||args[0]=="opencode"||args[0]=="--uninstall"))
        {
            if(AttachConsole(ATTACH_PARENT_PROCESS))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush=true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush=true });
            }
            return Cli.Run(args);
        }
        if(args != null && args.Length > 0 && args[0]=="--start-silent") { try { BotRuntime.Start(SettingsStore.Load()); return 0; } catch(Exception ex){File.AppendAllText(Path.Combine(ProductPaths.Home,"startup-error.log"),DateTime.Now.ToString("s")+" "+ex+Environment.NewLine);return 1;} }
#if SETUP_BUILD
        var setupApp=new Application(); setupApp.Run(new InstallerWindow()); return 0;
#else
        try { ProductRegistration.Register(ProductPaths.Root); } catch { }
        new DesktopApp().RunGui();
        return 0;
#endif
    }
}
