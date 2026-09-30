using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

[DataContract]
sealed class DesktopSettings
{
    public DesktopSettings()
    {
        BotToken = ""; AllowedUserId = ""; Locale = "zh"; Eot10Password = "";
        OpenCodeUrl = "http://127.0.0.1:4096"; OpenCodeServerVersion = "v1"; OpenCodeUsername = "opencode";
        OpenCodePassword = ""; ModelProvider = "opencode"; ModelId = "big-pickle";
        OpenCodeExecutable = ""; UseExistingOpenCode = true;
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
    [DataMember] public bool UseExistingOpenCode { get; set; }
}

static class ProductPaths
{
    public const string AppName = "OpenCode Telegram";
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
        env["OPENCODE_API_URL"] = s.OpenCodeUrl ?? "http://127.0.0.1:4096";
        env["OPENCODE_SERVER_USERNAME"] = s.OpenCodeUsername ?? "opencode";
        env["OPENCODE_SERVER_PASSWORD"] = s.OpenCodePassword ?? "";
        env["OPENCODE_MODEL_PROVIDER"] = s.ModelProvider ?? "opencode";
        env["OPENCODE_MODEL_ID"] = s.ModelId ?? "big-pickle";
        env["OPENCODE_TELEGRAM_EOT10_ENABLED"] = s.Eot10Enabled && !String.IsNullOrEmpty(s.Eot10Password) ? "1" : "0";
        env["OPENCODE_TELEGRAM_EOT10_PASSWORD"] = s.Eot10Enabled ? (s.Eot10Password ?? "") : "";
        env["OPENCODE_AUTO_RESTART_ENABLED"] = "true";
        if (!String.IsNullOrWhiteSpace(s.OpenCodeExecutable) && File.Exists(s.OpenCodeExecutable))
            env["OPENCODE_EXECUTABLE_PATH"] = s.OpenCodeExecutable;
        string openCodeDir = String.IsNullOrWhiteSpace(s.OpenCodeExecutable) ? "" : Path.GetDirectoryName(s.OpenCodeExecutable);
        env["PATH"] = ProductPaths.BotModulesBin + Path.PathSeparator + openCodeDir + Path.PathSeparator + (env["PATH"] ?? "");
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
    public static string Start(DesktopSettings settings) { return Execute("start --daemon --mode installed", settings, 60); }
    public static string Status(DesktopSettings settings) { return Execute("status", settings, 20); }
    public static string Stop(DesktopSettings settings) { return Execute("stop", settings, 30); }
    public static string InstallOpenCode(DesktopSettings settings)
    {
        if (!File.Exists(ProductPaths.NpmCli)) throw new FileNotFoundException("Bundled npm runtime is missing.", ProductPaths.NpmCli);
        string engineHome = Path.Combine(ProductPaths.Home, "opencode-runtime");
        Directory.CreateDirectory(engineHome);
        var psi = new ProcessStartInfo { FileName=ProductPaths.Node, Arguments="\""+ProductPaths.NpmCli+"\" install --prefix \""+engineHome+"\" --progress=true opencode-ai@latest", WorkingDirectory=engineHome, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        using(var process=Process.Start(psi))
        {
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync(); process.WaitForExit();
            if(process.ExitCode!=0) throw new InvalidOperationException((stdout.Result+Environment.NewLine+stderr.Result).Trim());
        }
        return Path.Combine(engineHome,"node_modules",".bin","opencode.cmd");
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

static class Cli
{
    const string Usage = "OpenCode Telegram Desktop\n\n" +
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
      "  environment                 Show installed runtime paths (no secrets)\n" +
      "  help                        Show this help\n\n" +
      "Configuration: GUI settings are protected using Windows CurrentUser DPAPI.\n" +
      "Environment: OPENCODE_TELEGRAM_ROOT, OPENCODE_TELEGRAM_HOME, PATH.\n" +
      "Version: 1.0.0";
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] == "gui") { new DesktopApp().RunGui(); return 0; }
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
            settings.OpenCodeExecutable = BotRuntime.InstallOpenCode(settings);
            settings.UseExistingOpenCode = false;
            settings.OpenCodeUrl = settings.OpenCodeServerVersion == "v2" ? "http://127.0.0.1:49374" : "http://127.0.0.1:4096";
            SettingsStore.Save(settings);
            Console.WriteLine("OpenCode installed: " + settings.OpenCodeExecutable);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("OpenCode install failed: " + ex.Message); return 1; }
    }
}

sealed class DesktopApp
{
    DesktopSettings settings;
    Window window;
    TextBox userIdBox, urlBox, modelProviderBox, modelIdBox, openCodePathBox;
    PasswordBox tokenBox, eotPasswordBox, serverPasswordBox;
    CheckBox eotEnabledBox, autoStartBox, useExistingBox;
    ComboBox serverVersionBox;
    TextBlock statusText;
    Grid content;
    StackPanel sidebar;
    Button activeNav;
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
        var w = new Window { Width = 1120, Height = 760, MinWidth = 980, MinHeight = 680, WindowStartupLocation = WindowStartupLocation.CenterScreen, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.CanResizeWithGrip, Title = ProductPaths.AppName };
        var shell = new Border { CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Color.FromRgb(246, 247, 251)), BorderBrush = new SolidColorBrush(Color.FromRgb(224, 227, 235)), BorderThickness = new Thickness(1), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.14 } };
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) }); root.RowDefinitions.Add(new RowDefinition());
        shell.Child = root; w.Content = shell;
        var titlebar = new Grid { Margin = new Thickness(22, 0, 18, 0) };
        titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); titlebar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(255, 95, 86)), Margin = new Thickness(0, 0, 8, 0) });
        dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(255, 189, 46)), Margin = new Thickness(0, 0, 8, 0) });
        dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(39, 201, 63)) });
        titlebar.Children.Add(dots);
        var title = Label("OpenCode Telegram", 14, true, Color.FromRgb(53, 58, 72)); title.HorizontalAlignment = HorizontalAlignment.Center; title.VerticalAlignment = VerticalAlignment.Center; titlebar.Children.Add(title); Grid.SetColumn(title, 0);
        var close = ActionButton("×", false); close.Width = 34; close.Height = 34; close.Click += (s,e) => w.Close(); titlebar.Children.Add(close); Grid.SetColumn(close, 1);
        titlebar.MouseLeftButtonDown += (s,e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
        root.Children.Add(titlebar); Grid.SetRow(titlebar, 0);

        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); body.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(body, 1); root.Children.Add(body);
        sidebar = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(239, 241, 247)), Margin = new Thickness(0, 0, 1, 0) };
        var brand = new StackPanel { Margin = new Thickness(22, 22, 16, 28) };
        brand.Children.Add(Label("OPENCODE", 11, true, Color.FromRgb(116, 124, 146)));
        brand.Children.Add(Label("Telegram", 22, true, Color.FromRgb(35, 41, 56)));
        sidebar.Children.Add(brand);
        string[] nav = { "概览", "机器人账号", "加密设置", "OpenCode 引擎", "启动与 CLI" };
        for (int i = 0; i < nav.Length; i++) { int page = i; var b = NavButton(nav[i], i == 0); b.Click += (s,e) => ShowPage(page); sidebar.Children.Add(b); if (i == 0) activeNav = b; }
        var sideFooter = new StackPanel { Margin = new Thickness(22, 0, 16, 20), VerticalAlignment = VerticalAlignment.Bottom };
        sideFooter.Children.Add(Label("独立桌面分叉", 11, false, Color.FromRgb(129, 137, 157)));
        sidebar.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { sideFooter } });
        body.Children.Add(sidebar); Grid.SetColumn(sidebar, 0);
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content = new Grid { Margin = new Thickness(36, 28, 36, 34) }; scroller.Content = content; body.Children.Add(scroller); Grid.SetColumn(scroller, 1);
        ShowPage(0);
        return w;
    }

    void ShowPage(int page)
    {
        selectedPage = page;
        content.Children.Clear();
        if (activeNav != null) SetNavStyle(activeNav, false);
        foreach (var child in sidebar.Children) if (child is Button) SetNavStyle((Button)child, false);
        activeNav = sidebar.Children.OfType<Button>().ElementAt(page); SetNavStyle(activeNav, true);
        if (page == 0) BuildDashboard(); else if (page == 1) BuildBotSettings(); else if (page == 2) BuildEncryptionSettings(); else if (page == 3) BuildOpenCodeSettings(); else BuildStartupSettings();
    }

    void BuildDashboard()
    {
        AddHeading("控制中心", "一个独立的 Windows 桌面客户端，管理 Bot、OpenCode 和 EOT10 加密。");
        var card = Card(); var stack = new StackPanel(); card.Child = stack;
        stack.Children.Add(Label("运行状态", 13, true, Color.FromRgb(112, 120, 140)));
        statusText = Label("正在读取 Bot 状态…", 20, true, Color.FromRgb(39, 46, 65)); stack.Children.Add(statusText);
        var row = Row(); var start = ActionButton("启动机器人", true); var stop = ActionButton("停止机器人", false); var refresh = ActionButton("刷新状态", false);
        start.Click += async (s,e) => await RunBotAction("start", true); stop.Click += async (s,e) => await RunBotAction("stop", false); refresh.Click += async (s,e) => await RefreshStatus();
        row.Children.Add(start); row.Children.Add(stop); row.Children.Add(refresh); stack.Children.Add(row); content.Children.Add(card);
        var hint = Card(); var hs = new StackPanel(); hint.Child=hs; hs.Children.Add(Label("快速开始",16,true,Color.FromRgb(39,46,65))); hs.Children.Add(Label("先绑定 Telegram Bot，再设置 OpenCode 地址和 EOT10 密码。所有凭据保存在当前 Windows 用户的 DPAPI 加密设置中。",13,false,Color.FromRgb(105,113,133))); content.Children.Add(hint);
        RefreshStatusOnDashboard();
    }

    async void RefreshStatusOnDashboard() { await RefreshStatus(); }

    void BuildBotSettings()
    {
        AddHeading("机器人账号", "绑定你自己的 Telegram Bot，并限制允许操作的用户。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        s.Children.Add(Label("Bot Token",12,true,Color.FromRgb(83,91,111))); tokenBox=new PasswordBox { Password=settings.BotToken, Height=40, FontSize=14, Padding=new Thickness(12,8,12,8), Margin=new Thickness(0,6,0,14), ToolTip="从 BotFather 获取" }; s.Children.Add(tokenBox);
        s.Children.Add(Label("允许的 Telegram User ID",12,true,Color.FromRgb(83,91,111))); userIdBox=new TextBox { Text=settings.AllowedUserId, Height=40, FontSize=14, Padding=new Thickness(12,8,12,8), Margin=new Thickness(0,6,0,14) }; s.Children.Add(userIdBox);
        s.Children.Add(Label("界面语言",12,true,Color.FromRgb(83,91,111))); var locale=new ComboBox { Height=36, Margin=new Thickness(0,6,0,16), ItemsSource=new string[]{"简体中文","English"}, SelectedIndex=settings.Locale=="en"?1:0 }; locale.SelectionChanged+=(a,b)=>settings.Locale=locale.SelectedIndex==1?"en":"zh"; s.Children.Add(locale);
        var save=ActionButton("保存机器人设置",true); save.Click+=(a,b)=>SaveSettings(true); s.Children.Add(save); content.Children.Add(card);
    }

    void BuildEncryptionSettings()
    {
        AddHeading("EOT10 加密", "在设备端和 Bot 之间加密消息正文与附件。密码使用当前 Windows 用户 DPAPI 保护。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        eotEnabledBox=new CheckBox { Content="启用 EOT10 加密", IsChecked=settings.Eot10Enabled, FontSize=16, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,4,0,14) }; s.Children.Add(eotEnabledBox);
        s.Children.Add(Label("加密密码（至少 12 个字符）",12,true,Color.FromRgb(83,91,111))); eotPasswordBox=new PasswordBox { Password=settings.Eot10Password, Height=40, Padding=new Thickness(12,8,12,8), Margin=new Thickness(0,6,0,14), FontSize=14 }; s.Children.Add(eotPasswordBox);
        var save=ActionButton("保存加密设置",true); save.Click+=(a,b)=>SaveSettings(true);
        var remove=ActionButton("删除保存的密码并关闭 EOT10",false); remove.Click+=(a,b)=>{ settings.Eot10Enabled=false; settings.Eot10Password=""; eotEnabledBox.IsChecked=false; eotPasswordBox.Clear(); SettingsStore.Save(settings); SetStatus("已删除本机 EOT10 密码。",false); };
        var row=Row(); row.Children.Add(save); row.Children.Add(remove); s.Children.Add(row); content.Children.Add(card);
    }

    void BuildOpenCodeSettings()
    {
        AddHeading("OpenCode 引擎", "连接现有 OpenCode，或安装独立的本地引擎。Bot 分叉本身不依赖上游 npm 包。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        useExistingBox=new CheckBox { Content="使用已安装 / 已运行的 OpenCode", IsChecked=settings.UseExistingOpenCode, FontSize=15, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,2,0,12) }; s.Children.Add(useExistingBox);
        s.Children.Add(Label("Server API 版本",12,true,Color.FromRgb(83,91,111))); serverVersionBox=new ComboBox { Height=36, Margin=new Thickness(0,5,0,12), ItemsSource=new string[]{"v1","v2"}, SelectedItem=settings.OpenCodeServerVersion??"v1" }; serverVersionBox.SelectionChanged+=(a,b)=>settings.OpenCodeServerVersion=serverVersionBox.SelectedItem as string??"v1"; s.Children.Add(serverVersionBox);
        s.Children.Add(Label("Server API URL",12,true,Color.FromRgb(83,91,111))); urlBox=new TextBox { Text=settings.OpenCodeUrl, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; s.Children.Add(urlBox);
        s.Children.Add(Label("OpenCode 可执行文件（使用现有安装时可留空）",12,true,Color.FromRgb(83,91,111))); openCodePathBox=new TextBox { Text=settings.OpenCodeExecutable, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; s.Children.Add(openCodePathBox);
        s.Children.Add(Label("Server 用户名",12,true,Color.FromRgb(83,91,111))); var serverUser=new TextBox { Text=settings.OpenCodeUsername, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; serverUser.TextChanged+=(a,b)=>settings.OpenCodeUsername=serverUser.Text; s.Children.Add(serverUser);
        s.Children.Add(Label("Server 密码",12,true,Color.FromRgb(83,91,111))); serverPasswordBox=new PasswordBox { Password=settings.OpenCodePassword, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; s.Children.Add(serverPasswordBox);
        s.Children.Add(Label("模型 Provider",12,true,Color.FromRgb(83,91,111))); modelProviderBox=new TextBox { Text=settings.ModelProvider, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; s.Children.Add(modelProviderBox);
        s.Children.Add(Label("模型 ID",12,true,Color.FromRgb(83,91,111))); modelIdBox=new TextBox { Text=settings.ModelId, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,16) }; s.Children.Add(modelIdBox);
        var save=ActionButton("保存引擎设置",true); save.Click+=(a,b)=>SaveSettings(true); var install=ActionButton("安装 OpenCode",false); install.Click+=async(a,b)=>await InstallOpenCode(); var row=Row(); row.Children.Add(save); row.Children.Add(install); s.Children.Add(row); content.Children.Add(card);
    }

    void BuildStartupSettings()
    {
        AddHeading("启动与命令行", "设置 Windows 登录启动，并使用 opencode-telegram 命令管理 Bot。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        autoStartBox=new CheckBox { Content="登录 Windows 时自动启动 Bot", IsChecked=StartupSetting.IsEnabled(), FontSize=15, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,2,0,14) }; s.Children.Add(autoStartBox);
        var save=ActionButton("保存启动选项",true); save.Click+=(a,b)=>{ StartupSetting.Set(autoStartBox.IsChecked==true); settings.AutoStart=autoStartBox.IsChecked==true; SettingsStore.Save(settings); SetStatus("开机自启动设置已保存。",false); }; s.Children.Add(save);
        s.Children.Add(Label("命令行（PowerShell / CMD）",12,true,Color.FromRgb(83,91,111)));
        var cmd=Card(); var cs=new StackPanel(); cmd.Child=cs; cs.Children.Add(Label("opencode-telegram help\nopencode-telegram status\nopencode-telegram start\nopencode-telegram stop\nopencode-telegram autostart enable|disable|status",13,false,Color.FromRgb(59,67,87))); content.Children.Add(card); content.Children.Add(cmd);
    }

    void AddHeading(string title,string subtitle) { var h=new StackPanel{Margin=new Thickness(0,0,0,22)}; h.Children.Add(Label(title,29,true,Color.FromRgb(34,40,56))); h.Children.Add(Label(subtitle,13,false,Color.FromRgb(111,119,138))); content.Children.Add(h); }
    Border Card() { return new Border { Background=Brushes.White, CornerRadius=new CornerRadius(18), Padding=new Thickness(22), Margin=new Thickness(0,0,0,16), BorderBrush=new SolidColorBrush(Color.FromRgb(230,232,238)), BorderThickness=new Thickness(1) }; }
    StackPanel Row() { return new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,10,0,0) }; }
    TextBlock Label(string text,double size,bool bold,Color color) { return new TextBlock { Text=text, FontFamily=new FontFamily("Segoe UI"), FontSize=size, FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal, Foreground=new SolidColorBrush(color), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,6) }; }
    Button ActionButton(string text,bool primary) { return new Button { Content=text, Padding=new Thickness(18,10,18,10), Margin=new Thickness(0,0,10,0), BorderThickness=new Thickness(0), Background=primary?new SolidColorBrush(Color.FromRgb(43,103,230)):new SolidColorBrush(Color.FromRgb(238,240,245)), Foreground=primary?Brushes.White:new SolidColorBrush(Color.FromRgb(52,60,78)), FontWeight=FontWeights.SemiBold, Cursor=Cursors.Hand }; }
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
        if (useExistingBox != null) settings.UseExistingOpenCode=useExistingBox.IsChecked==true;
        if (settings.Eot10Enabled && (settings.Eot10Password??"").Length<12) { MessageBox.Show("EOT10 密码至少需要 12 个字符。", ProductPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        SettingsStore.Save(settings); ConfigureUserEnvironment(settings);
        if (showStatus) SetStatus("设置已使用 Windows DPAPI 加密保存。",false);
        return true;
    }
    async Task RunBotAction(string action,bool saveFirst)
    {
        try
        {
            if(saveFirst && !SaveSettings(false)) return;
            if(action=="start" && (String.IsNullOrWhiteSpace(settings.BotToken) || String.IsNullOrWhiteSpace(settings.AllowedUserId))) throw new InvalidOperationException("请先在“机器人账号”页面填写 Bot Token 和允许的用户 ID。");
            SetStatus("正在执行 Bot 命令…",true);
            string result=await Task.Run(()=>BotRuntime.Execute(action,settings,60));
            SetStatus(String.IsNullOrWhiteSpace(result)?"命令已完成。":result,false);
        }
        catch(Exception ex) { SetStatus("操作失败："+ex.Message,true); }
    }
    async Task RefreshStatus() { try { string status=await Task.Run(()=>BotRuntime.Status(settings)); SetStatus(status,false); } catch(Exception ex){ SetStatus("状态读取失败："+ex.Message,true); } }
    async Task InstallOpenCode()
    {
        if (!SaveSettings(false)) return;
        SetStatus("正在下载并安装 OpenCode 引擎…",true);
        try
        {
            settings.OpenCodeExecutable=await Task.Run(()=>BotRuntime.InstallOpenCode(settings));
            settings.UseExistingOpenCode=false;
            settings.OpenCodeUrl=settings.OpenCodeServerVersion=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096";
            if(urlBox!=null) urlBox.Text=settings.OpenCodeUrl;
            if(openCodePathBox!=null) openCodePathBox.Text=settings.OpenCodeExecutable;
            SettingsStore.Save(settings);
            SetStatus("OpenCode 已安装。Bot 可通过 /opencode_start 启动本地 Server。",false);
        }
        catch(Exception ex) { SetStatus("OpenCode 安装失败："+ex.Message,true); }
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
    CheckBox autoStart;
    TextBlock status;
    public InstallerWindow()
    {
        Width=650; Height=490; WindowStartupLocation=WindowStartupLocation.CenterScreen; WindowStyle=WindowStyle.None; AllowsTransparency=true; Background=Brushes.Transparent; ResizeMode=ResizeMode.NoResize; Title="Install OpenCode Telegram";
        var border=new Border{CornerRadius=new CornerRadius(24),Background=new SolidColorBrush(Color.FromRgb(247,248,252)),Padding=new Thickness(34),BorderBrush=new SolidColorBrush(Color.FromRgb(226,229,237)),BorderThickness=new Thickness(1),Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=32,ShadowDepth=5,Opacity=.15}};
        var stack=new StackPanel(); border.Child=stack; Content=border;
        stack.Children.Add(new TextBlock{Text="OpenCode Telegram",FontSize=29,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(34,40,56)),Margin=new Thickness(0,4,0,8)});
        stack.Children.Add(new TextBlock{Text="独立桌面客户端 · Bot 源码与 Node 运行时内置",FontSize=14,Foreground=new SolidColorBrush(Color.FromRgb(108,116,137)),Margin=new Thickness(0,0,0,24)});
        stack.Children.Add(new TextBlock{Text="安装位置",FontSize=13,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,6)});
        target=new TextBox{Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","OpenCodeTelegram"),Height=40,Padding=new Thickness(12,8,12,8),FontSize=14}; stack.Children.Add(target);
        autoStart=new CheckBox{Content="登录 Windows 后自动启动 Bot",IsChecked=false,FontSize=14,Margin=new Thickness(0,18,0,15)}; stack.Children.Add(autoStart);
        status=new TextBlock{Text="包含独立 Bot、Node 与 CLI；OpenCode 引擎可选择使用现有实例。",FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(112,120,140)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,22)}; stack.Children.Add(status);
        var button=new Button{Content="安装并打开",Height=46,FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(43,103,230)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand}; button.Click+=async(s,e)=>await Install(); stack.Children.Add(button);
    }
    async Task Install()
    {
        string dest=target.Text.Trim();
        if(String.IsNullOrWhiteSpace(dest)){status.Text="请选择安装目录。";return;}
        try
        {
            status.Text="正在解压独立运行时…";
            await Task.Run(()=>{
                Directory.CreateDirectory(dest);
                using(Stream resource=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("BotRuntime.zip"))
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
                string installed=Path.Combine(dest,"OpenCodeTelegram.exe");
                if(!File.Exists(installed)) throw new FileNotFoundException("Installer payload is missing the desktop app executable.",installed);
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
                using(var shell=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)) { if(shell!=null&&autoStart.IsChecked==true)shell.SetValue("OpenCodeTelegram","\""+installed+"\" --start-silent"); }
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","OpenCode Telegram","OpenCode Telegram.lnk"),installed,dest);
            });
            status.Text="安装完成。正在打开控制面板…";
            Process.Start(new ProcessStartInfo{FileName=Path.Combine(dest,"OpenCodeTelegram.exe"),UseShellExecute=true});
            Close();
        }
        catch(Exception ex){status.Text="安装失败："+ex.Message;}
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
        shortcutType.InvokeMember("Description",System.Reflection.BindingFlags.SetProperty,null,shortcut,new object[]{"OpenCode Telegram desktop client"});
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
        if(args != null && args.Length > 0 && (args[0]=="help"||args[0]=="--help"||args[0]=="-h"||args[0]=="version"||args[0]=="--version"||args[0]=="status"||args[0]=="start"||args[0]=="stop"||args[0]=="autostart"||args[0]=="environment"||args[0]=="configure"||args[0]=="opencode"))
        {
            if(AttachConsole(ATTACH_PARENT_PROCESS))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush=true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush=true });
            }
            return Cli.Run(args);
        }
        if(args != null && args.Length > 0 && args[0]=="--start-silent") { try { BotRuntime.Start(SettingsStore.Load()); return 0; } catch(Exception ex){File.AppendAllText(Path.Combine(ProductPaths.Home,"startup-error.log"),DateTime.Now.ToString("s")+" "+ex+Environment.NewLine);return 1;} }
        bool installed=File.Exists(ProductPaths.BotCli)&&File.Exists(ProductPaths.Node);
        if(!installed) { var app=new Application(); app.Run(new InstallerWindow()); return 0; }
        new DesktopApp().RunGui();
        return 0;
    }
}
