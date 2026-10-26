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

static class Cli
{
    public static readonly string Usage = "opencode_telegram_bot\n\n" +
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
      "  opencode install [v2]       Install a local OpenCode V2 engine\n" +
      "  opencode scan               List detected OpenCode installations\n" +
      "  opencode uninstall [v2]     Remove an app-managed engine\n" +
      "  opencode select v2 [path]   Apply an OpenCode V2 executable\n" +
      "  opencode update             Check, download and apply a V2 update\n" +
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
      "Version: "+ProductPaths.Version;
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] == "gui") { new DesktopApp().RunGui(); return 0; }
        if (args[0] == "--uninstall") return Uninstall(args.Skip(1).Contains("--quiet"));
        if (args[0] == "help" || args[0] == "--help" || args[0] == "-h") { Console.WriteLine(Usage); return 0; }
        if (args[0] == "version" || args[0] == "--version") { Console.WriteLine(ProductPaths.Version); return 0; }
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
            case "opencode.version":if(value!="v2"){Console.Error.WriteLine("Only OpenCode V2 is supported.");return 2;}s.OpenCodeServerVersion="v2";break;
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
        var s=SettingsStore.Load();string version="v2";
        if(args[0]=="update")
        {
            string updated=BotRuntime.UpdateOpenCode(s,message=>Console.WriteLine(message));
            if(updated!=null)BotRuntime.ApplyOpenCode(s,updated);
            Console.WriteLine(s.Locale=="en"?(updated==null?"Already up to date.":"OpenCode updated and applied."):(updated==null?"已是最新版本。":"OpenCode 已更新并应用。"));return 0;
        }
        if(args.Length==0){Console.Error.WriteLine("Use: opencode-telegram opencode install|scan|uninstall|select");return 2;}
        if(args[0]=="install")return InstallOpenCode(args.Length>1?args[1]:null);
        if(args[0]=="scan")
        {
            foreach(var item in BotRuntime.ScanOpenCodeInstallations(s))Console.WriteLine(item.ApiVersion+"\t"+item.Version+"\t"+item.Executable+(item.IsRunning?"\trunning":""));
            return 0;
        }
        if(args[0]=="uninstall")
        {
            string target=args.Length>1?args[1]:version;if(target!="v2"){Console.Error.WriteLine("Only OpenCode V2 is supported.");return 2;}
            BotRuntime.UninstallOpenCode(target);if(s.OpenCodeServerVersion==target){s.OpenCodeExecutable="";SettingsStore.Save(s);}Console.WriteLine("Removed app-managed OpenCode "+target+".");return 0;
        }
        if(args[0]=="select")
        {
            if(args.Length<2||args[1]!="v2"){Console.Error.WriteLine("Use: opencode-telegram opencode select v2 [executable]");return 2;}
            string selected=args.Length>2?Path.GetFullPath(args[2]):BotRuntime.ResolveOpenCodeExecutable(s);BotRuntime.ApplyOpenCode(s,selected);Console.WriteLine("Selected OpenCode V2: "+s.OpenCodeExecutable);return 0;
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
            string version="v2",verb="serve",url=GetWebUrl(s);var uri=new Uri(url);
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
        return TelegramWebContext.TryLoad(ProductPaths.Home,out context)?context.WebAddress(address,settings.OpenCodeServerVersion):address;
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
            Console.WriteLine("OpenCode API version: v2");
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
            settings.OpenCodeServerVersion = "v2";
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
            if(!String.IsNullOrWhiteSpace(requestedVersion)&&requestedVersion!="v2")throw new InvalidOperationException("Only OpenCode V2 is supported.");settings.OpenCodeServerVersion="v2";
            settings.OpenCodeExecutable = BotRuntime.InstallOpenCode(settings,"v2",message=>Console.Error.WriteLine(message));
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
        if(quiet)return UninstallCore();
        bool finished=false;int result=1;
        var panel=new StackPanel{Margin=new Thickness(28)};
        var message=new TextBlock{Text=uninstallSettings.Locale=="en"?"Uninstalling and cleaning up related processes…":"正在卸载并清理相关进程…",TextWrapping=TextWrapping.Wrap,FontSize=15,Margin=new Thickness(0,0,0,18)};
        panel.Children.Add(message);panel.Children.Add(new ProgressBar{IsIndeterminate=true,Height=8});
        var progressWindow=new Window{Title=uninstallSettings.Locale=="en"?"Uninstalling":"正在卸载",Width=420,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterScreen,ResizeMode=ResizeMode.NoResize,Content=panel};
        progressWindow.Closing+=(s,e)=>{if(!finished)e.Cancel=true;};
        progressWindow.Loaded+=async(s,e)=>
        {
            try{result=await Task.Run(()=>UninstallCore());}
            catch(Exception ex){MessageBox.Show(progressWindow,(uninstallSettings.Locale=="en"?"Uninstall failed: ":"卸载失败：")+ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);}
            finally{finished=true;progressWindow.Close();}
        };
        progressWindow.ShowDialog();return result;
    }
    static int UninstallCore()
    {
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
