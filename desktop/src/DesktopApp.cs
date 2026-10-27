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

sealed class TelegramLoginCheck
{
    public TelegramLoginStatus Status;
    public long UserId;
    public string Detail;
}

enum TelegramLoginStatus
{
    Ok,
    NotLoggedIn,
    Network,
    Unavailable,
    InvalidAccount
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
    Button loginRetryButton;
    DispatcherTimer loginRetryTimer;
    bool loginRetryRunning;
    bool loginWarningActive;
    string loginWarningText;
    Button installV2Button, uninstallV2Button, scanOpenCodeButton, updateOpenCodeButton;
    StackPanel openCodeInstallationsPanel;
    bool openCodeOperationRunning;
    DispatcherTimer logTimer;
    string activeServiceLogPath;
    string logReadSignature;
    bool statusRefreshRunning;
    StackPanel content;
    ScrollViewer contentScroller;
    StackPanel sidebar;
    Button activeNav;
    string[] navLabels;
    readonly List<Button> navButtons = new List<Button>();
    TextBlock sidebarFooterText;
    int selectedPage;
    TextBox cliInput, cliOutput;
    Microsoft.Web.WebView2.Wpf.WebView2 embeddedBrowser;
    Microsoft.Web.WebView2.Core.CoreWebView2Environment webViewEnvironment;
    bool webConnectionRunning;
    Process cliProcess;
    PasswordBox cliSecretInput;
    Button cliRunButton,cliStopButton;
    readonly List<string> cliHistory=new List<string>();
    int cliHistoryIndex;
    string cliTranscript="";
    Process webServerProcess;
    TextBlock webStatus;
    MenuItem webStartButton, webStopButton;
    DispatcherTimer webContextTimer;
    TelegramWebContext telegramWebContext=new TelegramWebContext();
    TextBlock webContextStatus;
    Uri connectedWebServer;
    bool webContextRefreshRunning;
    TextBlock webContextHint;
    Grid webPageLayout;
    string webSyncFileSignature;
    DateTime webSyncCheckedAt;

    bool shutdownRunning,shutdownComplete;
    bool startupInProgress;
    bool wizardActive, wizardBusy;
    TextBlock wizardFeedback;
    string TgExecutable { get { string installed=Path.Combine(ProductPaths.Root,"bin","tg.exe");return File.Exists(installed)?installed:Path.Combine(ProductPaths.Root,"dependencies","tg","tg.exe"); } }
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
            bool cleanupFinished=false;
            var cleanupContent=new StackPanel{Margin=new Thickness(28)};
            cleanupContent.Children.Add(new TextBlock{Text=settings.Locale=="en"?"Cleaning up related processes…":"正在清理相关进程…",FontSize=16,Margin=new Thickness(0,0,0,18)});
            cleanupContent.Children.Add(new ProgressBar{IsIndeterminate=true,Height=6});
            var cleanupWindow=new Window{Owner=window,Title=settings.Locale=="en"?"Exiting":"正在退出",Width=360,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Content=cleanupContent};
            cleanupWindow.Closing+=(sender,args)=>{if(!cleanupFinished)args.Cancel=true;};
            cleanupWindow.Show();
            window.IsEnabled=false;
            try
            {
                if(logTimer!=null)logTimer.Stop();
                if(webContextTimer!=null)webContextTimer.Stop();
                await StopCliCommand();
                SetStatus(settings.Locale=="en"?"Stopping all services before exit…":"正在停止所有服务，完成后退出…",false);
                var idleSince=DateTime.UtcNow;
                while(serviceActionRunning||openCodeOperationRunning||webConnectionRunning)
                {
                    if((DateTime.UtcNow-idleSince).TotalSeconds>=5)break;
                    await Task.Delay(200);
                }
                await Task.Run(()=>
                {
                    // The Bot and the engine stop in parallel; the straggler sweep runs last so it
                    // cannot kill the `opencode service stop` process it is waiting for.
                    Task botStop=Task.Run(new Action(()=>{try{BotRuntime.Stop(settings);}catch{try{BotRuntime.StopCompetingBotProcesses();}catch{}}}));
                    Task engineStop=Task.Run(new Action(()=>{if(settings.OpenCodeServerVersion=="v2")try{BotRuntime.StopLocalV2Service(settings);}catch{}}));
                    Task.WaitAll(new[]{botStop,engineStop});
                    try{BotRuntime.StopOpenCodeProcesses();}catch{}
                });
                if(embeddedBrowser!=null){try{embeddedBrowser.Dispose();}catch{}embeddedBrowser=null;}
                cleanupFinished=true;cleanupWindow.Close();
                shutdownComplete=true;window.Close();
            }
            catch(Exception ex)
            {
                shutdownRunning=false;window.IsEnabled=true;
                cleanupFinished=true;cleanupWindow.Close();
                SetStatus((settings.Locale=="en"?"Unable to stop services: ":"停止服务失败：")+ex.Message,true);
                MessageBox.Show(window,(settings.Locale=="en"?"Unable to clean up related processes: ":"清理相关进程失败：")+ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);
            }
        };
        app.MainWindow = window;
        app.Run(window);
    }

    Window CreateWindow()
    {
        var w = new Window { Width = 1120, Height = 760, MinWidth = 980, MinHeight = 680, WindowStartupLocation = WindowStartupLocation.CenterScreen, WindowStyle = WindowStyle.SingleBorderWindow, Background = new SolidColorBrush(Color.FromRgb(246, 247, 251)), ResizeMode = ResizeMode.CanResize, Title = ProductPaths.AppName };
        var shell = new Border { Background = new SolidColorBrush(Color.FromRgb(246, 247, 251)) };
        var root = new Grid();
        shell.Child = root; w.Content = shell;
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); body.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(body);
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
        contentScroller=scroller;
        content = new StackPanel { Margin = new Thickness(36, 28, 36, 34) }; scroller.Content = content; body.Children.Add(scroller); Grid.SetColumn(scroller, 1);
        if(settings.SetupCompleted)
        {
            // The dashboard is usable at once; startup progress arrives through the status line.
            ShowPage(0);
            SetStatus(settings.Locale=="en"?"Starting OpenCode and the Bot…":"正在启动 OpenCode 与 Bot…",false);
        }
        else ShowSetupStep();
        w.Loaded += async (s,e) => { if(settings.SetupCompleted)await OpenHome(); };
        if(settings.SetupCompleted)PrewarmWebView();
        return w;
    }

    /// <summary>Creates the WebView2 environment while the services start, not on first page visit.</summary>
    async void PrewarmWebView()
    {
        if(webViewEnvironment!=null)return;
        try
        {
            webViewEnvironment=await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(BotRuntime.ResolveWebViewRuntime(),Path.Combine(ProductPaths.Home,"webview2"));
        }
        catch(Exception)
        {
            // The web page reports a missing runtime when the user opens it.
        }
    }

    async Task OpenHome()
    {
        try
        {
            startupInProgress=true;
            // The Telegram identity check talks to the network for up to 8 seconds; it runs
            // beside the service start instead of delaying it.
            string bound=settings.TelegramUserId;
            bool loginChecked=settings.Eot10Enabled&&!String.IsNullOrWhiteSpace(bound);
            Task<TelegramLoginCheck> loginCheck=loginChecked?CheckTelegramLogin():Task.FromResult(new TelegramLoginCheck{Status=TelegramLoginStatus.Ok});
            await Task.Run(()=>BotRuntime.Start(settings));
            if(loginChecked)
            {
                TelegramLoginCheck check=await loginCheck;
                string checkedId=check.UserId>0?check.UserId.ToString(CultureInfo.InvariantCulture):"";
                if(check.Status==TelegramLoginStatus.Ok&&(checkedId!=bound||settings.AllowedUserId!=bound))
                {
                    await Task.Run(new Action(()=>{try{BotRuntime.Stop(settings);}catch{try{BotRuntime.StopCompetingBotProcesses();}catch{}}}));
                    throw new InvalidOperationException(LoginMismatchMessage(checkedId));
                }
                if(check.Status!=TelegramLoginStatus.Ok)
                {
                    ShowPage(0);
                    ShowTelegramLoginWarning(check);
                    return;
                }
                ClearTelegramLoginWarning();
            }
            ShowPage(6);
        }
        catch(Exception ex)
        {
            ShowPage(0);
            SetStatus((settings.Locale=="en"?"Service startup failed: ":"服务启动失败：")+ex.Message,true);
            MessageBox.Show(window,ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally { startupInProgress=false; window.IsEnabled=true; if(serviceToggle!=null)serviceToggle.IsEnabled=true; }
    }

    string LoginMismatchMessage(string checkedId)
    {
        bool en=settings.Locale=="en";
        string expected=String.IsNullOrWhiteSpace(settings.TelegramUserId)?"?":settings.TelegramUserId;
        string actual=String.IsNullOrWhiteSpace(checkedId)?"?":checkedId;
        return en
            ? "Telegram signed-in account ("+actual+") does not match the account bound during setup ("+expected+"). Run tg login with the bound account, then retry."
            : "Telegram 当前登录账号（"+actual+"）与向导绑定的账号（"+expected+"）不一致。请通过 tg login 登录回绑定的账号后重试。";
    }

    string TelegramLoginFailureText(TelegramLoginCheck check)
    {
        bool en=settings.Locale=="en";
        if(check.Status==TelegramLoginStatus.Network)
            return en?"Cannot reach Telegram to verify the account. Check network/VPN/proxy, then retry.":"无法连接 Telegram 验证账号。请检查网络/VPN/代理后重试。";
        if(check.Status==TelegramLoginStatus.NotLoggedIn)
            return en?"Not signed in. Complete tg login and retry.":"尚未登录，请完成 tg login 后重试。";
        if(check.Status==TelegramLoginStatus.InvalidAccount)
            return en?"tg is signed in to a bot account. Sign in with your personal account.":"tg 当前登录的是机器人账号，请改用个人账号登录。";
        return (en?"Could not verify Telegram login: ":"无法验证 Telegram 登录：")+(check.Detail??"");
    }

    void ShowTelegramLoginWarning(TelegramLoginCheck check)
    {
        bool en=settings.Locale=="en";
        loginWarningActive=true;
        string reason;
        if(check.Status==TelegramLoginStatus.Network)
            reason=en
                ?"Cannot reach Telegram to verify the signed-in account (network or VPN/proxy is down). The Bot keeps running; retrying every 15 seconds."
                :"无法连接 Telegram 验证登录账号（网络未连接或 VPN/代理未开启）。Bot 仍在运行，每 15 秒自动重试。";
        else if(check.Status==TelegramLoginStatus.NotLoggedIn)
            reason=en
                ?"Telegram is not signed in. Run tg login in a terminal, then retry. The Bot keeps running, but encrypted transport may be unavailable."
                :"Telegram 尚未登录。请在终端运行 tg login 后重试。Bot 仍在运行，但 EOT10 加密传输可能不可用。";
        else
            reason=(en?"Could not verify the Telegram login: ":"无法验证 Telegram 登录：")+(check.Detail??"")+(en?". The Bot keeps running; retrying every 15 seconds.":"。Bot 仍在运行，每 15 秒自动重试。");

        loginWarningText=reason;
        SetStatus(reason,true);
        if(loginRetryButton!=null)
        {
            loginRetryButton.Visibility=Visibility.Visible;
            loginRetryButton.Content=settings.Locale=="en"?"Retry verification":"重试验证";
        }
        StartTelegramLoginRetryTimer();
    }

    void ClearTelegramLoginWarning()
    {
        loginWarningActive=false;
        loginWarningText=null;
        if(loginRetryTimer!=null)loginRetryTimer.Stop();
        if(loginRetryButton!=null)loginRetryButton.Visibility=Visibility.Collapsed;
    }

    void StartTelegramLoginRetryTimer()
    {
        if(loginRetryTimer==null)
        {
            loginRetryTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(15)};
            loginRetryTimer.Tick+=async(s,e)=>await RetryTelegramLogin();
        }
        loginRetryTimer.Start();
    }

    async Task RetryTelegramLogin()
    {
        if(!loginWarningActive||loginRetryRunning||startupInProgress)return;
        loginRetryRunning=true;
        if(loginRetryButton!=null){loginRetryButton.IsEnabled=false;loginRetryButton.Content=settings.Locale=="en"?"Verifying…":"正在验证…";}
        try
        {
            TelegramLoginCheck check=await CheckTelegramLogin();
            if(!loginWarningActive)return;
            string bound=settings.TelegramUserId;
            string checkedId=check.UserId>0?check.UserId.ToString(CultureInfo.InvariantCulture):"";
            if(check.Status!=TelegramLoginStatus.Ok)
            {
                ShowTelegramLoginWarning(check);
                return;
            }

            if(checkedId!=bound||settings.AllowedUserId!=bound)
            {
                ClearTelegramLoginWarning();
                await Task.Run(new Action(()=>{try{BotRuntime.Stop(settings);}catch{try{BotRuntime.StopCompetingBotProcesses();}catch{}}}));
                string message=LoginMismatchMessage(checkedId);
                SetStatus((settings.Locale=="en"?"Service stopped: ":"服务已停止：")+message,true);
                MessageBox.Show(window,message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);
                return;
            }

            ClearTelegramLoginWarning();
            SetStatus(settings.Locale=="en"?"Telegram account verification recovered.":"Telegram 账号验证已恢复。",false);
        }
        catch(Exception ex)
        {
            SetStatus((settings.Locale=="en"?"Telegram account verification failed: ":"Telegram 账号验证失败：")+ex.Message,true);
        }
        finally
        {
            loginRetryRunning=false;
            if(loginRetryButton!=null){loginRetryButton.IsEnabled=true;loginRetryButton.Content=settings.Locale=="en"?"Retry verification":"重试验证";}
        }
    }

    async Task<TelegramLoginCheck> CheckTelegramLogin()
    {
        return await Task.Run(()=>
        {
            try
            {
                using(var process=new Process { StartInfo=new ProcessStartInfo { FileName=TgExecutable,Arguments="whoami -o json",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } })
                {
                    process.Start();
                    var output=process.StandardOutput.ReadToEndAsync();
                    var error=process.StandardError.ReadToEndAsync();
                    if(!process.WaitForExit(8000))
                    {
                        try{process.Kill();}catch{}
                        return new TelegramLoginCheck{Status=TelegramLoginStatus.Network,Detail="tg whoami timed out"};
                    }
                    Task.WaitAll(output,error);
                    string stdout=output.Result??"";
                    string stderr=error.Result??"";
                    if(process.ExitCode!=0)return ClassifyTelegramLoginFailure(stdout+"\n"+stderr,process.ExitCode);
                    try
                    {
                        using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(stdout)))
                        {
                            var identity=(TelegramIdentityResult)new DataContractJsonSerializer(typeof(TelegramIdentityResult)).ReadObject(stream);
                            if(identity==null||identity.Data==null||identity.Data.Id<=0)return new TelegramLoginCheck{Status=TelegramLoginStatus.NotLoggedIn,Detail="tg whoami returned no account"};
                            if(identity.Data.Bot)return new TelegramLoginCheck{Status=TelegramLoginStatus.InvalidAccount,Detail="tg is signed in to a bot account"};
                            return new TelegramLoginCheck{Status=TelegramLoginStatus.Ok,UserId=identity.Data.Id};
                        }
                    }
                    catch(Exception ex)
                    {
                        return new TelegramLoginCheck{Status=TelegramLoginStatus.Unavailable,Detail=ex.Message};
                    }
                }
            }
            catch(Exception ex)
            {
                return new TelegramLoginCheck{Status=TelegramLoginStatus.Unavailable,Detail=ex.Message};
            }
        });
    }

    static TelegramLoginCheck ClassifyTelegramLoginFailure(string text,int exitCode)
    {
        string lower=(text??"").ToLowerInvariant();
        string[] loginMarkers={"auth_key","unauthorized","not authorized","not logged","no active session","session not found","session revoked","login required","user_deactivated","401"};
        foreach(string marker in loginMarkers)
            if(lower.Contains(marker))return new TelegramLoginCheck{Status=TelegramLoginStatus.NotLoggedIn,Detail=FirstTelegramLine(text)};
        string[] networkMarkers={"timeout","timed out","deadline","connection","network","proxy","dial","tls","eof","unreachable","unavailable","internet","refused","reset"};
        foreach(string marker in networkMarkers)
            if(lower.Contains(marker))return new TelegramLoginCheck{Status=TelegramLoginStatus.Network,Detail=FirstTelegramLine(text)};
        string detail=FirstTelegramLine(text);
        return new TelegramLoginCheck{Status=TelegramLoginStatus.Unavailable,Detail=(detail.Length>0?detail:"tg exited with code "+exitCode)};
    }

    static string FirstTelegramLine(string text)
    {
        if(String.IsNullOrWhiteSpace(text))return "";
        string[] lines=text.Trim().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
        string line=lines.Length>0?lines[0]:"";
        return line.Length>180?line.Substring(0,180):line;
    }

    void ShowSetupStep()
    {
        wizardActive=true;
        int step=Math.Max(0,Math.Min(3,settings.SetupStep));settings.SetupStep=step;
        ShowPage(step==0?0:step);
        sidebar.IsEnabled=false;
        if(logTimer!=null)logTimer.Stop();
        if(step==0)content.Children.Clear();
        var heading=Label(settings.Locale=="en"?"First-run setup · "+(step+1)+" / 4":"首次使用向导 · "+(step+1)+" / 4",23,true,Color.FromRgb(39,46,65));
        content.Children.Insert(0,heading);
        string[] instructions=settings.Locale=="en"?new[]{
            "Step 1: Sign in with your personal Telegram account. No API ID or API hash is needed; the bundled official CLI handles initialization. If you do not have Telegram, install it from https://telegram.org and sign in on your phone first.",
            "Step 2: Open Telegram, search @BotFather (the official verified account), tap Start and send /newbot. Give your Bot a name and a unique username ending in bot. Paste the returned Token below. Then search @userinfobot, tap Start and copy your personal numeric ID into Allowed Telegram User ID. These are not your phone number or your Bot ID. Continue saves both; keep the Token secret. After setup, open your new Bot and tap Start to control OpenCode.",
            "Step 3: Ordinary Telegram users should leave encryption OFF and Continue. To enable it, first install the customized Android client using the download link below, enable EOT10 there, and set exactly the same password here and there (at least 12 characters). Standard Telegram cannot read encrypted messages. Continue saves your choice; you can change it later in Encryption settings.",
            "Step 4: Scanning starts automatically. If you are unsure, use the blue Bundled engine button: it is included and needs no download. Otherwise Apply an existing V2 engine from the detected list. Applied means that engine is selected. Leave advanced fields unchanged unless needed. Finish starts the Bot and OpenCode and opens the web homepage. Future launches do this automatically; the wizard will not appear again."
        }:new[]{
            "第 1 步：登录你自己的 Telegram 个人账号。无需申请 API ID/API Hash，软件会自动初始化内置 tg。还没有 Telegram？先从 https://telegram.org 安装手机客户端并登录，再点击下面的登录按钮。",
            "第 2 步：打开 Telegram，搜索官方认证账号 @BotFather，点击开始并发送 /newbot。按提示填写机器人名称和以 bot 结尾的唯一用户名，将返回的 Token 粘贴到下方。再搜索 @userinfobot，点击开始，将回复的个人数字 ID 填入允许的 Telegram User ID（不是手机号，也不是机器人 ID）。点击继续保存；不要把 Token 发给别人。向导结束后打开刚创建的机器人并点击开始，即可遥控 OpenCode。",
            "第 3 步：使用普通 Telegram，保持加密关闭，直接点击继续。需要加密时，先通过下方下载链接安装定制安卓客户端，在客户端开启 EOT10，并在两端设置完全相同的密码（至少 12 个字符）。普通 Telegram 无法读取加密消息。点击继续保存选择，以后可在加密设置中修改。",
            "第 4 步：软件会自动扫描可用环境。不知道选哪个？选择蓝色的内置引擎，无需另外下载。想用已有 OpenCode V2，点击对应的应用按钮；显示正在应用表示已选中。高级设置一般无需修改。点击完成后自动启动 Bot 和 OpenCode，直接进入网页首页；以后每次打开都自动启动，不再出现向导。"
        };
        var explanation=Label(instructions[step],14,false,Color.FromRgb(83,91,111));explanation.Margin=new Thickness(0,14,0,20);content.Children.Insert(1,explanation);
        if(step==1&&userIdBox!=null&&!String.IsNullOrWhiteSpace(settings.TelegramUserId))
        {
            userIdBox.Text=settings.TelegramUserId;userIdBox.IsReadOnly=true;
            content.Children.Insert(2,Label(settings.Locale=="en"?"Your personal user ID is filled automatically from tg whoami and locked to the signed-in account. Only the Bot Token needs to be pasted. To change accounts, return to step 1.":"个人用户 ID 已从 tg whoami 自动获取并锁定为登录账号，无需再找 @userinfobot 或手动填写。你只需粘贴 Bot Token；更换账号请返回第一步。",14,false,Color.FromRgb(33,128,74)));
        }
        if(step==0)
        {
            content.Children.Add(Label(settings.Locale=="en"?"Sign in to Telegram":"登录 Telegram",19,true,Color.FromRgb(39,46,65)));
            content.Children.Add(Label(settings.Locale=="en"?"Open the login window and scan the QR code with Telegram (Settings → Devices → Link Desktop Device). Enter any 2FA password only in that window. Return here and Continue to verify login. Existing sessions are reused.":"打开登录窗口，用手机 Telegram 扫描二维码（设置 → 设备 → 连接桌面设备）。两步验证密码仅在该窗口输入。登录完成后返回这里点击继续，向导会检查登录状态；已有登录可直接继续。",14,false,Color.FromRgb(83,91,111)));
            var login=ActionButton(settings.Locale=="en"?"Open Telegram login":"打开 Telegram 登录窗口",true);
            login.Click+=(a,b)=>{try{Process.Start(new ProcessStartInfo { FileName="powershell.exe",Arguments="-NoProfile -NoExit -Command \"if (!(Test-Path (Join-Path $env:APPDATA 'gotd\\gotd.cli.yaml'))) { & '"+TgExecutable.Replace("'","''")+"' init; if ($LASTEXITCODE -ne 0) { return } }; & '"+TgExecutable.Replace("'","''")+"' login\"",UseShellExecute=true });}catch(Exception ex){wizardFeedback.Text=ex.Message;}};
            content.Children.Add(login);
        }
        wizardFeedback=Label("",13,false,Color.FromRgb(191,55,55));content.Children.Add(wizardFeedback);
        var row=Row();var back=ActionButton(settings.Locale=="en"?"Back":"上一步",false);back.IsEnabled=step>0;
        back.Click+=(a,b)=>{if(wizardBusy||openCodeOperationRunning)return;if(!SaveSettings(false))return;settings.SetupStep--;SettingsStore.Save(settings);ShowSetupStep();};row.Children.Add(back);
        var next=ActionButton(settings.Locale=="en"?(step==3?"Finish and open OpenCode":"Continue"):(step==3?"完成并进入 OpenCode":"继续"),true);
        next.Click+=async(a,b)=>
        {
            if(wizardBusy||openCodeOperationRunning)return;
            wizardBusy=true;next.IsEnabled=false;back.IsEnabled=false;
            try
            {
                if(step==0)
                {
                    TelegramLoginCheck check=await CheckTelegramLogin();
                    if(check.Status!=TelegramLoginStatus.Ok)
                    {
                        wizardFeedback.Text=TelegramLoginFailureText(check);
                        return;
                    }
                    settings.TelegramUserId=check.UserId.ToString(CultureInfo.InvariantCulture);
                    settings.AllowedUserId=settings.TelegramUserId;
                }
                if(step>0&&!SaveSettings(false))return;
                if(step==1&&(!Regex.IsMatch(settings.BotToken??"",@"^\d+:[A-Za-z0-9_-]+$")||!Regex.IsMatch(settings.AllowedUserId??"",@"^[1-9]\d*$"))){wizardFeedback.Text=settings.Locale=="en"?"Enter a valid Bot Token and personal user ID.":"请填写有效的 Bot Token 和个人用户数字 ID。";return;}
                if(step==1||step==3)
                {
                    TelegramLoginCheck check=await CheckTelegramLogin();
                    if(check.Status!=TelegramLoginStatus.Ok)
                    {
                        wizardFeedback.Text=TelegramLoginFailureText(check);
                        return;
                    }
                    string checkedId=check.UserId.ToString(CultureInfo.InvariantCulture);
                    if(settings.AllowedUserId!=checkedId)
                    {
                        wizardFeedback.Text=settings.Locale=="en"?"The bound user must match tg whoami. Go back to Telegram login and bind the same personal account.":"绑定用户必须与 tg whoami 的登录账号一致。请返回 Telegram 登录步骤，使用同一个个人账号绑定。";
                        return;
                    }
                    settings.TelegramUserId=checkedId;
                }
                if(step==3)
                {
                    if(!File.Exists(BotRuntime.ResolveOpenCodeExecutable(settings))){wizardFeedback.Text=settings.Locale=="en"?"Select an available OpenCode engine first.":"请先选择可用的 OpenCode 引擎。";return;}
                    settings.SetupCompleted=true;SettingsStore.Save(settings);wizardActive=false;sidebar.IsEnabled=true;await OpenHome();
                }
                else {settings.SetupStep=step+1;SettingsStore.Save(settings);ShowSetupStep();}
            }
            catch(Exception ex){wizardFeedback.Text=ex.Message;}
            finally {wizardBusy=false;next.IsEnabled=true;back.IsEnabled=step>0;}
        };
        row.Children.Add(next);content.Children.Add(row);
    }

    void ShowPage(int page)
    {
        selectedPage = page;
        contentScroller.Content=content;
        contentScroller.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        if(logTimer!=null) logTimer.Stop();
        if(webContextTimer!=null)webContextTimer.Stop();
        content.Children.Clear();
        tokenBox=null;userIdBox=null;eotEnabledBox=null;eotPasswordBox=null;
        urlBox=null;serverVersionBox=null;modelProviderBox=null;modelIdBox=null;openCodePathBox=null;serverPasswordBox=null;
        if (activeNav != null) SetNavStyle(activeNav, false);
        foreach (var child in sidebar.Children) if (child is Button) SetNavStyle((Button)child, false);
        activeNav = sidebar.Children.OfType<Button>().ElementAt(page); SetNavStyle(activeNav, true);
        logReadSignature=null;
        if (page == 0) { BuildDashboard(); logTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)}; logTimer.Tick+=(s,e)=>RefreshStatus(); logTimer.Start(); } else if (page == 1) BuildBotSettings(); else if (page == 2) BuildEncryptionSettings(); else if (page == 3) BuildOpenCodeSettings(); else if(page==4) BuildStartupSettings(); else if(page==5) BuildCliConsole(); else if(page==6) BuildOpenCodeWeb(); else if(page==7) BuildLanguageSettings();
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
        var row = Row(); serviceToggle=ActionButton("启动服务", true); serviceToggle.MinWidth=150; serviceToggle.IsEnabled=!startupInProgress; var refresh = ActionButton("刷新状态", false);
        loginRetryButton=ActionButton(settings.Locale=="en"?"Retry verification":"重试验证",false); loginRetryButton.Visibility=loginWarningActive?Visibility.Visible:Visibility.Collapsed; loginRetryButton.Click+=async(s,e)=>await RetryTelegramLogin();
        serviceToggle.Click += async (s,e) => await RunBotAction(serviceToggle.Tag as string??"start", true); refresh.Click += (s,e) => RefreshStatus();
        row.Children.Add(serviceToggle); row.Children.Add(refresh); row.Children.Add(loginRetryButton); stack.Children.Add(row); content.Children.Add(card);
        if(loginWarningActive&&!String.IsNullOrWhiteSpace(loginWarningText))SetStatus(loginWarningText,true);
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
        var notice=Label(settings.Locale=="en"
            ? "Encryption is off by default. EOT10 requires the specially customized encrypted Telegram Android client; the standard Telegram app cannot encrypt or decrypt these messages. Set the same encryption password in the customized client and this Bot before enabling it."
            : "加密默认关闭。启用 EOT10 必须使用专门定制的加密 Telegram 安卓客户端，普通 Telegram 无法加密或解密这些消息。开启前，请在定制客户端和本 Bot 中设置相同的加密密码。",13,false,Color.FromRgb(83,91,111));
        notice.Margin=new Thickness(0,18,0,8); content.Children.Add(notice);
        var download=new TextBlock { FontSize=13, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,8) };
        var link=new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(settings.Locale=="en"?"Customized Android client on GitHub (v11.0.22)":"定制安卓客户端 GitHub 下载（v11.0.22）"))
        {
            NavigateUri=new Uri("https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/tag/desktop-1.0.15-android-11.0.22"),
        };
        link.RequestNavigate+=(sender,args)=>{try{Process.Start(new ProcessStartInfo { FileName=args.Uri.AbsoluteUri,UseShellExecute=true });}catch(Exception ex){MessageBox.Show((settings.Locale=="en"?"Unable to open the download page: ":"无法打开下载页面：")+ex.Message,ProductPaths.AppName);}args.Handled=true;};
        download.Inlines.Add(link);content.Children.Add(download);
    }

    void BuildOpenCodeSettings()
    {
        settings.OpenCodeUrl=BotRuntime.ResolveOpenCodeUrl(settings);
        AddHeading("OpenCode 引擎", "连接现有 OpenCode，或安装独立的本地引擎。Bot 分叉本身不依赖上游 npm 包。");
        var card=Card(); var s=new StackPanel(); card.Child=s;
        s.Children.Add(Label(settings.Locale=="en"?"Before starting, all local OpenCode processes are stopped and the selected API version is started.":"启动服务时会先停止本机所有 OpenCode 进程，再按所选 API 版本启动服务。",12,false,Color.FromRgb(105,113,133)));
        settings.OpenCodeServerVersion="v2";
        s.Children.Add(Label(settings.Locale=="en"?"OpenCode V2 only":"仅支持 OpenCode V2",12,true,Color.FromRgb(83,91,111))); serverVersionBox=new ComboBox { Height=36, Margin=new Thickness(0,5,0,12), ItemsSource=new string[]{"v2"}, SelectedItem="v2", Visibility=Visibility.Collapsed }; s.Children.Add(serverVersionBox);
        apiVersionHint=Label("",12,false,Color.FromRgb(105,113,133));apiVersionHint.Margin=new Thickness(0,0,0,10);s.Children.Add(apiVersionHint);UpdateApiVersionHint();
        var advanced=new Expander { Header=settings.Locale=="en"?"Advanced settings":"高级设置", IsExpanded=false, Margin=new Thickness(0,4,0,8) };
        var advancedContent=new StackPanel { Margin=new Thickness(4,12,4,4) }; advanced.Content=advancedContent;
        advancedContent.Children.Add(Label("Server API URL",12,true,Color.FromRgb(83,91,111))); urlBox=new TextBox { Text=settings.OpenCodeUrl, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; advancedContent.Children.Add(urlBox);
        advancedContent.Children.Add(Label("可选：OpenCode 可执行文件（通常无需手动填写，点击扫描自动识别）",12,true,Color.FromRgb(83,91,111))); var exeRow=Row(); openCodePathBox=new TextBox { Text=settings.OpenCodeExecutable, Height=38, MinWidth=560, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,8,12) }; exeRow.Children.Add(openCodePathBox); var pickExe=ActionButton("浏览…",false); pickExe.Click+=(a,b)=>{var dialog=new OpenFileDialog{Filter="OpenCode executable|opencode.exe;opencode.cmd;*.exe;*.cmd|All files|*.*",CheckFileExists=true};if(dialog.ShowDialog()==true)openCodePathBox.Text=dialog.FileName;}; exeRow.Children.Add(pickExe); advancedContent.Children.Add(exeRow);
        scanOpenCodeButton=ActionButton(settings.Locale=="en"?"Scan OpenCode":"扫描 OpenCode",false); scanOpenCodeButton.Click+=async(a,b)=>await ScanExistingOpenCode();
        updateOpenCodeButton=ActionButton(settings.Locale=="en"?"Update OpenCode":"更新 OpenCode",false); updateOpenCodeButton.Click+=async(a,b)=>await UpdateOpenCode();
        var scanRow=Row(); scanRow.Children.Add(scanOpenCodeButton); scanRow.Children.Add(updateOpenCodeButton); s.Children.Add(scanRow);
        openCodeInstallationsPanel=new StackPanel(); s.Children.Add(openCodeInstallationsPanel);
        advancedContent.Children.Add(Label("Server 用户名",12,true,Color.FromRgb(83,91,111))); var serverUser=new TextBox { Text=settings.OpenCodeUsername, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; serverUser.TextChanged+=(a,b)=>settings.OpenCodeUsername=serverUser.Text; advancedContent.Children.Add(serverUser);
        advancedContent.Children.Add(Label("Server 密码（V2 服务必填；可用 opencode service get password 查看）",12,true,Color.FromRgb(83,91,111))); serverPasswordBox=new PasswordBox { Password=settings.OpenCodePassword, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; advancedContent.Children.Add(serverPasswordBox);
        advancedContent.Children.Add(Label("模型 Provider",12,true,Color.FromRgb(83,91,111))); modelProviderBox=new TextBox { Text=settings.ModelProvider, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,12) }; advancedContent.Children.Add(modelProviderBox);
        advancedContent.Children.Add(Label("模型 ID",12,true,Color.FromRgb(83,91,111))); modelIdBox=new TextBox { Text=settings.ModelId, Height=38, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,5,0,16) }; advancedContent.Children.Add(modelIdBox);
        var save=ActionButton("保存引擎设置",true); save.Click+=(a,b)=>SaveSettings(true);
        installV2Button=ActionButton("安装 OpenCode（API v2）",false); installV2Button.Click+=async(a,b)=>await InstallOpenCode("v2");
        var installRow=Row(); installRow.Children.Add(save); installRow.Children.Add(installV2Button); advancedContent.Children.Add(installRow);
        uninstallV2Button=ActionButton(settings.Locale=="en"?"Uninstall OpenCode (API v2)":"卸载 OpenCode（API v2）",false); uninstallV2Button.Click+=async(a,b)=>await UninstallOpenCode("v2");
        uninstallV2Button.Background=new SolidColorBrush(Color.FromRgb(191,55,55)); uninstallV2Button.Foreground=Brushes.White;
        var uninstallRow=Row(); uninstallRow.Children.Add(uninstallV2Button); advancedContent.Children.Add(uninstallRow);
        openCodeFeedback=Label("",13,false,Color.FromRgb(105,113,133)); openCodeFeedback.Margin=new Thickness(0,14,0,4); advancedContent.Children.Add(openCodeFeedback);
        s.Children.Add(advanced);
        var visibleSave=ActionButton("保存引擎设置",true);visibleSave.Click+=(a,b)=>SaveSettings(true);s.Children.Add(visibleSave);
        advancedContent.Children.Remove(openCodeFeedback);s.Children.Add(openCodeFeedback);
        UpdateOpenCodeEngineButtons(); content.Children.Add(card);
        content.Dispatcher.BeginInvoke(new Action(async()=>await ScanExistingOpenCode(false)));
    }

    void UpdateSelectedOpenCodeVersion()
    {
        string next="v2";
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
        if(settings.Locale!="en")details.Children.Add(Label("Bot：start 启动，stop 停止，status 查看状态，logs [--lines N] 查看日志。\n交互配置：configure bot 设置机器人账号；configure eot10 enable|disable 设置加密；configure opencode 设置引擎和模型；configure show 查看非敏感配置。\n设置：config set <key> <value> 修改配置；config unset <key> 删除可选配置。\n引擎：仅支持 V2。opencode install [v2] 安装，scan 扫描，select v2 [path] 应用，update 检查并更新当前应用版本，uninstall [v2] 删除应用管理的引擎。\n网页：web start|stop|status|open 启动、停止、查看或打开网页服务。\n登录启动：autostart enable|disable|status 控制 Bot；app-autostart enable|disable|status 控制桌面软件。\n其他：language zh|en 切换语言，environment 查看运行路径，help 查看帮助，version 查看版本；gui 打开桌面窗口；--uninstall 卸载桌面软件。",13,false,Color.FromRgb(83,91,111)));
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
        if(webPageLayout!=null)
        {
            // The page, the browser and the timer survive page switches: only reattach them.
            contentScroller.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;
            contentScroller.Content=webPageLayout;
            if(webContextTimer!=null)webContextTimer.Start();
            var cachedBrowser=embeddedBrowser;
            window.Dispatcher.BeginInvoke(new Action(async()=>
            {
                if(selectedPage!=6||embeddedBrowser!=cachedBrowser||shutdownRunning||startupInProgress)return;
                if(cachedBrowser.CoreWebView2==null)await StartOpenCodeWeb("v2",new Uri(BotRuntime.ResolveOpenCodeUrl(settings)));
                else await RefreshTelegramWebContext();
            }));
            return;
        }
        if(webServerProcess==null){int runningPid;webServerProcess=Cli.TryReadWebProcess(Path.Combine(ProductPaths.Home,"web-server.pid"),out runningPid);}
        string version="v2";
        Uri address; string url=BotRuntime.ResolveOpenCodeUrl(settings);if(!Uri.TryCreate(url,UriKind.Absolute,out address))address=new Uri(version=="v2"?"http://127.0.0.1:49374":"http://127.0.0.1:4096");
        connectedWebServer=null;
        bool en=settings.Locale=="en";
        var layout=new Grid{Margin=new Thickness(8)};
        layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());
        var toolbar=new Grid{Margin=new Thickness(4,0,0,6)};toolbar.ColumnDefinitions.Add(new ColumnDefinition());toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var title=Label(en?"OpenCode Web":"OpenCode 网页端",13,true,Color.FromRgb(59,67,87));title.VerticalAlignment=VerticalAlignment.Center;toolbar.Children.Add(title);
        var menu=new ContextMenu();
        webStartButton=new MenuItem{Header=en?"Reconnect":"重新连接"};webStartButton.Click+=async(a,b)=>await StartOpenCodeWeb(version,address);
        var webRefresh=new MenuItem{Header=en?"Refresh page":"刷新网页"};webRefresh.Click+=async(a,b)=>{if(embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null)embeddedBrowser.CoreWebView2.Reload();else await StartOpenCodeWeb(version,address);};
        var openExternal=new MenuItem{Header=en?"Open in default browser":"在默认浏览器打开"};openExternal.Click+=async(a,b)=>await OpenExternalWeb(version,address);
        webStopButton=new MenuItem{Header=en?"Stop web service":"停止网页服务"};webStopButton.IsEnabled=webServerProcess!=null&&!webServerProcess.HasExited;webStopButton.Click+=(a,b)=>StopOpenCodeWeb();
        menu.Items.Add(webStartButton);menu.Items.Add(webRefresh);menu.Items.Add(openExternal);menu.Items.Add(webStopButton);menu.Items.Add(new Separator());
        var details=new StackPanel{MaxWidth=420};details.Children.Add(Label("OpenCode "+version.ToUpperInvariant()+" · "+address,12,false,Color.FromRgb(83,91,111)));
        webStatus=Label(en?"Connecting…":"正在连接…",12,false,Color.FromRgb(105,113,133));details.Children.Add(webStatus);
        webContextStatus=Label(en?"Reading shared workspace…":"正在读取共享工作区…",12,false,Color.FromRgb(83,91,111));details.Children.Add(webContextStatus);
        webContextHint=Label("",11,false,Color.FromRgb(105,113,133));details.Children.Add(webContextHint);menu.Items.Add(new MenuItem{Header=details,IsEnabled=false});
        var more=new Button{Content="⋯",Width=36,Height=30,FontSize=20,Padding=new Thickness(0),ToolTip=en?"Web options":"网页选项",ContextMenu=menu};
        more.Click+=(a,b)=>{menu.PlacementTarget=more;menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;menu.IsOpen=true;};Grid.SetColumn(more,1);toolbar.Children.Add(more);layout.Children.Add(toolbar);
        embeddedBrowser=new Microsoft.Web.WebView2.Wpf.WebView2();Grid.SetRow(embeddedBrowser,1);layout.Children.Add(embeddedBrowser);
        contentScroller.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;contentScroller.Content=layout;webPageLayout=layout;
        var browser=embeddedBrowser;
        browser.SourceChanged+=(a,b)=>{if(embeddedBrowser==browser)UpdateTelegramWebContextStatus();};
        browser.NavigationCompleted+=async(a,b)=>{if(embeddedBrowser!=browser)return;webSyncFileSignature=null;if(!b.IsSuccess)webStatus.Text=(settings.Locale=="en"?"Web page failed to load: ":"网页加载失败：")+b.WebErrorStatus+(settings.Locale=="en"?". Reconnect or open in your default browser.":"。请重新连接或在默认浏览器打开。");else await RefreshTelegramWebContext();};
        browser.Loaded+=async(a,b)=>{if(embeddedBrowser!=browser)return;await RefreshTelegramWebContext();if(embeddedBrowser==browser&&browser.CoreWebView2==null)await StartOpenCodeWeb(version,address);};
        webContextTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
        webContextTimer.Tick+=async(a,b)=>await RefreshTelegramWebContext();webContextTimer.Start();
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
            string statePath=Path.Combine(ProductPaths.Home,"desktop-sync","state.json");
            long stateLength=0;
            DateTime stateWriteTime=DateTime.MinValue;
            bool stateExists=await Task.Run(new Func<bool>(()=>{
                try{var info=new FileInfo(statePath);if(!info.Exists)return false;stateLength=info.Length;stateWriteTime=info.LastWriteTimeUtc;return true;}
                catch(IOException){return false;}
            }));
            string fileSignature=(stateExists?stateLength.ToString(CultureInfo.InvariantCulture)+":"+stateWriteTime.Ticks.ToString(CultureInfo.InvariantCulture):"missing");
            // The Bot rewrites the file on every change and at a 2-second heartbeat; a 500 ms
            // poll only stats it, so the browser is not re-synchronized needlessly.
            if(fileSignature==webSyncFileSignature&&(DateTime.UtcNow-webSyncCheckedAt).TotalMilliseconds<3000)
            {
                UpdateTelegramWebContextStatus();
                return;
            }
            webSyncFileSignature=fileSignature;
            webSyncCheckedAt=DateTime.UtcNow;

            TelegramWebContext context=null;string json=null;
            bool loaded=await Task.Run(()=>TelegramWebContext.TryLoadSync(ProductPaths.Home,out context,out json));
            if(selectedPage!=6||webContextStatus!=status)return;
            if(!loaded)
            {
                webContextStatus.Text=settings.Locale=="en"?"Workspace sync is unavailable.":"工作区同步暂时不可用。";
                webContextHint.Text=settings.Locale=="en"?"Start or reconnect the bot. Your current page is kept.":"请启动或重新连接机器人；保留当前网页，不强制跳转。";
                return;
            }
            telegramWebContext=context;
            UpdateTelegramWebContextStatus();
            var browser=embeddedBrowser;
            if(connectedWebServer!=null&&browser!=null&&browser.CoreWebView2!=null)
            {
                Uri current;if(!Uri.TryCreate(browser.CoreWebView2.Source,UriKind.Absolute,out current)||current.GetLeftPart(UriPartial.Authority)!=connectedWebServer.GetLeftPart(UriPartial.Authority))return;
                string target=BotRuntime.AddWebAuthToken(context.WebAddress(connectedWebServer,settings.OpenCodeServerVersion),settings).ToString();
                await browser.CoreWebView2.ExecuteScriptAsync("window.__opencodeTelegramSync && window.__opencodeTelegramSync.accept("+json+","+JsonString(target)+","+JsonString(settings.OpenCodeServerVersion)+","+JsonString(settings.Locale)+")");
            }
        }
        finally{webContextRefreshRunning=false;}
    }
    void UpdateTelegramWebContextStatus()
    {
        if(selectedPage!=6||webContextStatus==null)return;
        bool en=settings.Locale=="en";
        string hint=telegramWebContext.Busy?(en?"AI is responding. Project/session switching is blocked until it finishes.":"AI 正在回答，暂时不能切换项目或会话；回答结束后自动解除限制。"):
            telegramWebContext.MobileActive?(en?"Following the phone's current operation.":"手机正在操作，正在跟随手机的当前操作。"):
            (en?"Two-way sync · You can browse and choose projects while idle.":"双向同步 · 当前空闲，可以浏览并选择项目。");
        string text;
        if(!telegramWebContext.HasSession)
        {
            string directory=telegramWebContext.Project==null?null:telegramWebContext.Project.Directory;
            text=(en?"Telegram project: ":"Telegram 项目：")+(String.IsNullOrWhiteSpace(directory)?(en?"Not selected":"未选择"):directory)+Environment.NewLine+(en?"Telegram session: Not selected":"Telegram 会话：未选择");
        }
        else
        {
            string title=String.IsNullOrWhiteSpace(telegramWebContext.Session.Title)?telegramWebContext.Session.Id:telegramWebContext.Session.Title;
            text=(en?"Telegram project: ":"Telegram 项目：")+telegramWebContext.Session.Directory+Environment.NewLine+(en?"Telegram session: ":"Telegram 会话：")+title;
        }
        if(webContextStatus.Text!=text)webContextStatus.Text=text;
        webContextStatus.ToolTip=text;
        if(webContextHint.Text!=hint)webContextHint.Text=hint;
        webContextHint.Foreground=new SolidColorBrush(telegramWebContext.Busy||telegramWebContext.MobileActive?Color.FromRgb(173,101,26):Color.FromRgb(105,113,133));
    }
    static string JsonString(string value)
    {
        using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(string)).WriteObject(stream,value);return Encoding.UTF8.GetString(stream.ToArray());}
    }
    async Task ReceiveDesktopSyncCommand(Microsoft.Web.WebView2.Wpf.WebView2 browser,string source,string json)
    {
        try
        {
            Uri origin;if(browser!=embeddedBrowser||connectedWebServer==null||!Uri.TryCreate(source,UriKind.Absolute,out origin)||origin.GetLeftPart(UriPartial.Authority)!=connectedWebServer.GetLeftPart(UriPartial.Authority))return;
            var match=Regex.Match(json,"\"id\"\\s*:\\s*\"([a-zA-Z0-9-]{1,80})\"");if(!match.Success)return;
            string id=match.Groups[1].Value;
            await Task.Run(()=>
            {
                string directory=Path.Combine(ProductPaths.Home,"desktop-sync");Directory.CreateDirectory(directory);
                string file=Path.Combine(directory,"command-"+id+".json"),temp=file+".tmp";
                File.WriteAllText(temp,json,new UTF8Encoding(false));File.Move(temp,file);
            });
        }
        catch(Exception ex){if(webContextHint!=null)webContextHint.Text=(settings.Locale=="en"?"Synchronization failed: ":"同步失败：")+ex.Message;}
    }

    async Task StartOpenCodeWeb(string version,Uri address)
    {
        if(shutdownRunning||startupInProgress||webConnectionRunning||embeddedBrowser==null)return;
        webConnectionRunning=true;
        var browser=embeddedBrowser;
        webStartButton.IsEnabled=false;
        webStatus.Text=settings.Locale=="en"?"Connecting to OpenCode…":"正在连接 OpenCode…";
        try
        {
            if(browser.CoreWebView2==null)
            {
                var environment=webViewEnvironment??await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(BotRuntime.ResolveWebViewRuntime(),Path.Combine(ProductPaths.Home,"webview2"));
                webViewEnvironment=environment;
                if(embeddedBrowser!=browser)return;
                await browser.EnsureCoreWebView2Async(environment);
                if(embeddedBrowser!=browser)return;
                using(var script=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("EmbeddedWebSync.js"))
                using(var reader=new StreamReader(script,Encoding.UTF8))await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(reader.ReadToEnd());
                browser.CoreWebView2.WebMessageReceived+=async(sender,eventArgs)=>await ReceiveDesktopSyncCommand(browser,eventArgs.Source,eventArgs.WebMessageAsJson);
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
            if(embeddedBrowser!=null&&webStartButton!=null)webStartButton.IsEnabled=true;
            if(embeddedBrowser!=null&&embeddedBrowser!=browser&&selectedPage==6&&!shutdownRunning)
                QueueWebReconnect();
        }
        await RefreshTelegramWebContext();
    }
    void QueueWebReconnect()
    {
        window.Dispatcher.BeginInvoke(new Action(async()=>
        {
            if(selectedPage==6&&embeddedBrowser!=null&&!shutdownRunning)
                await StartOpenCodeWeb("v2",new Uri(BotRuntime.ResolveOpenCodeUrl(settings)));
        }));
    }
    void NavigateWeb(Uri address,bool followTelegram=false)
    {
        Uri target=telegramWebContext.WebAddress(address,settings.OpenCodeServerVersion);
        Uri current;
        if(!followTelegram&&connectedWebServer!=null&&embeddedBrowser!=null&&embeddedBrowser.CoreWebView2!=null&&Uri.TryCreate(embeddedBrowser.CoreWebView2.Source,UriKind.Absolute,out current)&&current.GetLeftPart(UriPartial.Authority)==address.GetLeftPart(UriPartial.Authority))
            target=new Uri(current.GetLeftPart(UriPartial.Path)+current.Fragment);
        connectedWebServer=address;
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
                Uri serviceUrl=null;
                bool reused=await Task.Run(()=>BotRuntime.TryUseRunningV2Service(settings,out serviceUrl));
                if(!reused)serviceUrl=await Task.Run(()=>BotRuntime.StartLocalV2Service(settings));
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
            Uri target=telegramWebContext.WebAddress(server,version);
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
        bool en=settings.Locale=="en";
        AddHeading(en?"Language & Appearance":"语言与外观",en?"Choose a language. Changes are saved and applied immediately.":"选择界面语言，选择后立即保存并应用。");
        var card=Card();var s=new StackPanel();card.Child=s;
        s.Children.Add(Label(en?"Interface language":"界面语言",14,true,Color.FromRgb(39,46,65)));
        languagePicker=new ComboBox{Width=220,Height=36,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,12,0,12),ItemsSource=new string[]{"简体中文","English"},SelectedIndex=en?1:0};
        languagePicker.SelectionChanged+=(sender,args)=>{if(languagePicker.SelectedIndex<0)return;string locale=languagePicker.SelectedIndex==1?"en":"zh";if(settings.Locale==locale)return;string previous=settings.Locale;settings.Locale=locale;try{SettingsStore.Save(settings);ApplyLanguage();ShowPage(selectedPage);}catch(Exception ex){settings.Locale=previous;MessageBox.Show((en?"Unable to save language settings: ":"无法保存语言设置：")+ex.Message,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);}};
        s.Children.Add(languagePicker);
        s.Children.Add(Label(en?"Saved in the encrypted configuration for the current Windows user.":"设置会保存在当前 Windows 用户的加密配置中。",13,false,Color.FromRgb(105,113,133)));
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
        settings.OpenCodeServerVersion="v2";
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
        // Never start, stop or restart beside the startup that is already running.
        if(serviceActionRunning||startupInProgress)return;
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
            RefreshStatus();
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
    void RefreshStatus()
    {
        if(statusRefreshRunning||serviceActionRunning||startupInProgress)return;
        statusRefreshRunning=true;
        try
        {
            string serviceStatus=BotRuntime.ReadServiceStatus();
            if(serviceActionRunning)return;
            // Keep the last visible status while polling or awaiting a usable result.
            if(String.IsNullOrWhiteSpace(serviceStatus))return;
            if(loginWarningActive&&!String.IsNullOrWhiteSpace(loginWarningText))SetStatus(loginWarningText,true);
            else SetStatus(serviceStatus,false);
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
        if(String.IsNullOrWhiteSpace(activeServiceLogPath)){logView.Text="Logs will appear here after the service starts.";logReadSignature=null;return;}
        try
        {
            var info=new FileInfo(activeServiceLogPath);
            if(!info.Exists){logView.Text="Waiting for the service to write its first log entry…";logReadSignature=null;return;}
            // The dashboard polls every two seconds: only the changed tail is read again.
            string signature=info.Length.ToString(CultureInfo.InvariantCulture)+":"+info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            if(signature==logReadSignature)return;
            logReadSignature=signature;
            const int tailBytes=64*1024;
            const int tailLines=250;
            string text;
            using(var stream=new FileStream(activeServiceLogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                if(stream.Length>tailBytes)
                {
                    stream.Seek(stream.Length-tailBytes,SeekOrigin.Begin);
                    using(var skip=new StreamReader(stream,Encoding.UTF8,true,1024,true))skip.ReadLine();
                }
                using(var reader=new StreamReader(stream,Encoding.UTF8))
                    text=reader.ReadToEnd();
            }
            var tail=new Queue<string>();
            foreach(string line in text.Replace("\r\n","\n").Split('\n'))
            {
                if(tail.Count==tailLines)tail.Dequeue();
                tail.Enqueue(line);
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
            ConfigureUserEnvironment(settings);
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
                string status=await Task.Run(new Func<string>(()=>BotRuntime.ReadServiceStatus()));
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
            ConfigureUserEnvironment(settings);
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
    async Task ScanExistingOpenCode(bool report=true)
    {
        openCodeOperationRunning=true;
        UpdateOpenCodeEngineButtons();
        if(report)SetOpenCodeFeedback(settings.Locale=="en"?"Scanning process paths, PATH, npm/Bun globals, Windows install records, and the app's engine folders…":"正在扫描运行进程、各级 PATH、npm/Bun 全局目录、Windows 安装记录和应用内引擎目录…",false);
        try
        {
            var installations=await Task.Run(()=>BotRuntime.ScanOpenCodeInstallations(settings));
            RenderOpenCodeInstallations(installations);
            if(installations.Count==0)
            {
                string message=settings.Locale=="en"?"No OpenCode V2 CLI was found. Install OpenCode V2 from this page.":"没有扫描到 OpenCode V2 CLI，请在本页安装 OpenCode V2。";
                if(report)SetOpenCodeFeedback(message,true);
                return;
            }
            if(report)SetOpenCodeFeedback(settings.Locale=="en"?"Scan complete. Apply a version below.":"扫描完成，请在列表中应用所需版本。",false);
            return;
        }
        catch(Exception ex)
        {
            SetOpenCodeFeedback((settings.Locale=="en"?"OpenCode scan failed: ":"扫描 OpenCode 失败：")+ex.Message,true);
        }
        finally { openCodeOperationRunning=false; UpdateOpenCodeEngineButtons(); }
    }
    async Task ApplyOpenCodeInstallation(OpenCodeInstallation installation)
    {
        if(openCodeOperationRunning)return;
        openCodeOperationRunning=true; UpdateOpenCodeEngineButtons();
        try
        {
            if(installation==null)
            {
                SetOpenCodeFeedback(settings.Locale=="en"?"OpenCode selection cancelled.":"已取消 OpenCode 选择。",false);
                return;
            }
            await Task.Run(()=>BotRuntime.ApplyOpenCode(settings,installation.Executable));
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
            string errorMessage=settings.Locale=="en"?"Unable to apply OpenCode: "+ex.Message:"应用 OpenCode 失败："+ex.Message;
            SetOpenCodeFeedback(errorMessage,true);
            MessageBox.Show(errorMessage,ProductPaths.AppName,MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            openCodeOperationRunning=false;
            UpdateOpenCodeEngineButtons();
        }
        await ScanExistingOpenCode(false);
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
    void RenderOpenCodeInstallations(List<OpenCodeInstallation> installations)
    {
        if(openCodeInstallationsPanel==null)return;
        openCodeInstallationsPanel.Children.Clear();
        string selected=BotRuntime.ResolveOpenCodeExecutable(settings);
        foreach(var item in installations)
        {
            var entry=new StackPanel { Margin=new Thickness(0,10,0,10) };
            var title=Row(); title.Children.Add(Label("OpenCode "+item.Version,14,true,Color.FromRgb(39,46,65)));
            if(item.IsBundled)title.Children.Add(new Border { Background=new SolidColorBrush(Color.FromRgb(43,103,230)), CornerRadius=new CornerRadius(4), Padding=new Thickness(8,3,8,3), Margin=new Thickness(8,0,0,0), Child=new TextBlock { Text=settings.Locale=="en"?"Bundled":"内置", Foreground=Brushes.White } });
            entry.Children.Add(title); entry.Children.Add(Label(item.Executable,12,false,Color.FromRgb(105,113,133)));
            bool active=String.Equals(selected,item.Executable,StringComparison.OrdinalIgnoreCase);
            var actions=Row(); var apply=ActionButton(settings.Locale=="en"?(active?"Applied":"Apply"):(active?"正在应用":"应用"),false);
            // Applied wins over the bundled hint: green always means "this engine is in use".
            apply.Background=new SolidColorBrush(active?Color.FromRgb(33,128,74):item.IsBundled?Color.FromRgb(43,103,230):Color.FromRgb(112,120,140)); apply.Foreground=Brushes.White;
            apply.IsEnabled=!active; apply.Click+=async(a,b)=>await ApplyOpenCodeInstallation(item); actions.Children.Add(apply);
            var remove=ActionButton(settings.Locale=="en"?"Uninstall":"卸载",false); remove.Background=new SolidColorBrush(Color.FromRgb(191,55,55)); remove.Foreground=Brushes.White;
            string managed=Path.GetFullPath(Path.Combine(ProductPaths.Home,"opencode-runtime","v2"))+Path.DirectorySeparatorChar;
            bool owned=item.IsBundled||Path.GetFullPath(item.Executable).StartsWith(managed,StringComparison.OrdinalIgnoreCase);
            remove.Click+=async(a,b)=>{if(owned)await RemoveOpenCodeInstallation(item);else {MessageBox.Show(settings.Locale=="en"?"Remove this external installation using its original installer or package manager. Windows installed apps will now open.":"请使用原安装程序或包管理器卸载此外部版本，即将打开 Windows 已安装的应用。",ProductPaths.AppName);Process.Start(new ProcessStartInfo { FileName="ms-settings:appsfeatures",UseShellExecute=true });}};
            if(!wizardActive)actions.Children.Add(remove); entry.Children.Add(actions); openCodeInstallationsPanel.Children.Add(entry);
        }
    }
    async Task UpdateOpenCode()
    {
        if(openCodeOperationRunning)return;
        if(!SaveSettings(false))return;
        openCodeOperationRunning=true; UpdateOpenCodeEngineButtons();
        SetOpenCodeFeedback(settings.Locale=="en"?"Checking the applied OpenCode version…":"正在检查当前应用的 OpenCode 版本…",false);
        try
        {
            string executable=await Task.Run(()=>BotRuntime.UpdateOpenCode(settings,message=>SetOpenCodeFeedback(message,false)));
            if(executable!=null)
            {
                await Task.Run(()=>BotRuntime.ApplyOpenCode(settings,executable));
                if(openCodePathBox!=null)openCodePathBox.Text=executable;
                if(urlBox!=null)urlBox.Text=settings.OpenCodeUrl;
                if(serverPasswordBox!=null)serverPasswordBox.Password=settings.OpenCodePassword??"";
                SettingsStore.Save(settings); ConfigureUserEnvironment(settings);
            }
            SetOpenCodeFeedback(settings.Locale=="en"?(executable==null?"The applied version is up to date.":"OpenCode updated and applied."):(executable==null?"当前应用版本已是最新。":"OpenCode 已更新并应用。"),false);
        }
        catch(Exception ex){SetOpenCodeFeedback((settings.Locale=="en"?"Update failed: ":"更新失败：")+ex.Message,true);}
        finally {openCodeOperationRunning=false;UpdateOpenCodeEngineButtons();}
        await ScanExistingOpenCode(false);
    }
    async Task RemoveOpenCodeInstallation(OpenCodeInstallation installation)
    {
        if(openCodeOperationRunning)return;
        string prompt=(settings.Locale=="en"?"Uninstall this OpenCode installation?\n":"确定卸载这个 OpenCode 版本？\n")+installation.Executable;
        if(MessageBox.Show(prompt,ProductPaths.AppName,MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        openCodeOperationRunning=true;UpdateOpenCodeEngineButtons();
        try
        {
            bool active=String.Equals(BotRuntime.ResolveOpenCodeExecutable(settings),installation.Executable,StringComparison.OrdinalIgnoreCase);
            if(active){await Task.Run(()=>BotRuntime.Stop(settings));await Task.Run(()=>BotRuntime.StopLocalV2Service(settings));}
            await Task.Run(()=>BotRuntime.UninstallOpenCodeInstallation(installation));
            if(active)settings.OpenCodeExecutable="";
            SettingsStore.Save(settings);ConfigureUserEnvironment(settings);
            if(openCodePathBox!=null)openCodePathBox.Text=settings.OpenCodeExecutable;
            SetOpenCodeFeedback(settings.Locale=="en"?"OpenCode uninstalled.":"OpenCode 已卸载。",false);
        }
        catch(Exception ex){SetOpenCodeFeedback((settings.Locale=="en"?"Uninstall failed: ":"卸载失败：")+ex.Message,true);}
        finally {openCodeOperationRunning=false;UpdateOpenCodeEngineButtons();}
        await ScanExistingOpenCode(false);
    }
    void UpdateOpenCodeEngineButtons()
    {
        if(installV2Button==null) return;
        installV2Button.IsEnabled=!openCodeOperationRunning;
        uninstallV2Button.IsEnabled=!openCodeOperationRunning&&Directory.Exists(Path.Combine(ProductPaths.Home,"opencode-runtime","v2"));
        scanOpenCodeButton.IsEnabled=!openCodeOperationRunning;
        if(updateOpenCodeButton!=null)updateOpenCodeButton.IsEnabled=!openCodeOperationRunning;
        if(openCodeInstallationsPanel!=null)openCodeInstallationsPanel.IsEnabled=!openCodeOperationRunning;
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
        BotRuntime.ConfigureOpenCodeCommand(s);
        items.RemoveAll(x=>String.Equals(x.TrimEnd('\\','/'),bin.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase));
        items.Insert(0,bin);
        Environment.SetEnvironmentVariable("PATH",String.Join(Path.PathSeparator.ToString(),items),EnvironmentVariableTarget.User);
        try { using(var key=Registry.CurrentUser.OpenSubKey("Environment",true)) { SendMessageTimeout(new IntPtr(0xffff),0x001A,IntPtr.Zero,"Environment",0x0002,5000,IntPtr.Zero); } } catch { }
    }
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Auto)] static extern IntPtr SendMessageTimeout(IntPtr hWnd,uint msg,IntPtr wParam,string lParam,uint flags,uint timeout,IntPtr result);
}
