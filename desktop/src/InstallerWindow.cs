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

sealed class InstallerWindow : Window
{
    TextBox target;
    TextBlock status;
    StackPanel installerStack;
    CheckBox launchAfterInstall;
    string installedExe;
    bool english;
    ProgressBar installProgress;
    bool installing;
    public InstallerWindow()
    {
        english=SettingsStore.Load().Locale=="en";
        Closing+=(s,e)=>{if(installing)e.Cancel=true;};
        BuildInstallerForm();
    }
    void BuildInstallerForm()
    {
        string selectedPath=target==null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","OpenCodeTelegram"):target.Text;
        Width=650; Height=550; WindowStartupLocation=WindowStartupLocation.CenterScreen; WindowStyle=WindowStyle.SingleBorderWindow; Background=new SolidColorBrush(Color.FromRgb(247,248,252)); ResizeMode=ResizeMode.NoResize; Title=ProductPaths.AppName;
        var border=new Border{CornerRadius=new CornerRadius(24),Background=new SolidColorBrush(Color.FromRgb(247,248,252)),Padding=new Thickness(34),BorderBrush=new SolidColorBrush(Color.FromRgb(226,229,237)),BorderThickness=new Thickness(1),Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=32,ShadowDepth=5,Opacity=.15}};
        installerStack=new StackPanel(); border.Child=installerStack; Content=border;
        var languageRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,16)};
        languageRow.Children.Add(new TextBlock{Text="语言 / Language",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,0,12)});
        var language=new ComboBox{Width=180,Height=32,ItemsSource=new string[]{"简体中文","English"},SelectedIndex=english?1:0};
        language.SelectionChanged+=(s,e)=>{if(installing||language.SelectedIndex<0)return;bool next=language.SelectedIndex==1;if(next==english)return;english=next;BuildInstallerForm();};
        languageRow.Children.Add(language);installerStack.Children.Add(languageRow);
        installerStack.Children.Add(new TextBlock{Text=ProductPaths.AppName,FontSize=29,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(34,40,56)),Margin=new Thickness(0,4,0,8)});
        installerStack.Children.Add(new TextBlock{Text=english?"OpenCode installer and launcher · Telegram remote control · encrypted communication":"OpenCode 安装与启动器 · Telegram 远程控制 · 加密通信",FontSize=14,Foreground=new SolidColorBrush(Color.FromRgb(108,116,137)),Margin=new Thickness(0,0,0,24),TextWrapping=TextWrapping.Wrap});
        installerStack.Children.Add(new TextBlock{Text=english?"Install location":"安装位置",FontSize=13,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,6)});
        target=new TextBox{Text=selectedPath,Height=40,Padding=new Thickness(12,8,12,8),FontSize=14};
        var pathRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,16)}; target.Margin=new Thickness(0,0,8,0); target.Width=470; pathRow.Children.Add(target);
        var browse=new Button{Content=english?"Browse…":"浏览…",Width=88,Height=40}; browse.Click+=(s,e)=>{using(var dialog=new Forms.FolderBrowserDialog()){dialog.Description=english?"Choose the installation folder":"选择安装文件夹";dialog.SelectedPath=target.Text;if(dialog.ShowDialog()==Forms.DialogResult.OK)target.Text=dialog.SelectedPath;}}; pathRow.Children.Add(browse); installerStack.Children.Add(pathRow);
        status=new TextBlock{Text=english?"The setup installs the app and creates Start menu and desktop shortcuts. You can choose whether to launch it when setup finishes.":"安装程序会安装应用并创建开始菜单和桌面快捷方式。安装结束时可选择是否立即启动。",FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(112,120,140)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,22)}; installerStack.Children.Add(status);
        installProgress=new ProgressBar{Height=8,Minimum=0,Maximum=100,Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,16)};installerStack.Children.Add(installProgress);
        var button=new Button{Content=english?"Install":"安装",Height=46,FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(43,103,230)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand}; button.Click+=async(s,e)=>await Install(); installerStack.Children.Add(button);
    }
    async Task Install()
    {
        string dest=target.Text.Trim();
        if(installing)return;
        if(String.IsNullOrWhiteSpace(dest)){status.Text=english?"Choose an installation folder.":"请选择安装目录。";return;}
        installing=true;installProgress.Visibility=Visibility.Visible;installProgress.IsIndeterminate=true;
        foreach(var control in installerStack.Children.OfType<UIElement>())if(control!=status&&control!=installProgress)control.IsEnabled=false;
        try
        {
            bool enableAppStartup=AppStartupSetting.IsEnabled();
            bool enableBotStartup=StartupSetting.IsEnabled();
            bool wasBotRunning=false;
            var currentSettings=SettingsStore.Load();
            currentSettings.Locale=english?"en":"zh";SettingsStore.Save(currentSettings);
            status.Text=english?"Stopping the running Bot…":"正在停止已运行的 Bot…";
            await Task.Run(()=>{try { wasBotRunning=BotRuntime.Status(currentSettings).IndexOf("Service status: running",StringComparison.OrdinalIgnoreCase)>=0; } catch { } if(wasBotRunning) { try { BotRuntime.Execute("stop",currentSettings,20); } catch { } }});
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
                    {
                    long total=archive.Entries.Sum(entry=>entry.Length),completed=0;int lastPercent=-1;
                    foreach(var entry in archive.Entries)
                    {
                        string output=Path.GetFullPath(Path.Combine(dest,entry.FullName));
                        if(!output.StartsWith(Path.GetFullPath(dest)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe installer entry.");
                        if(String.IsNullOrEmpty(entry.Name)){Directory.CreateDirectory(output);continue;}
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        entry.ExtractToFile(output,true);
                        completed+=entry.Length;
                        int percent=total==0?90:(int)(completed*90/total);
                        if(percent!=lastPercent){lastPercent=percent;Dispatcher.Invoke(new Action(()=>{installProgress.IsIndeterminate=false;installProgress.Value=percent;status.Text=(english?"Extracting runtime: ":"正在解压运行时：")+percent+"%";}));}
                    }
                    }
                }
                Dispatcher.Invoke(new Action(()=>{installProgress.Value=90;status.Text=english?"Configuring the app and creating shortcuts…":"正在配置应用并创建快捷方式…";}));
                BotRuntime.RemoveLegacyOpenCodePayload(dest);
                BotRuntime.ConfigureFixedWebViewPermissions(dest);
                installedExe=Path.Combine(dest,"OpenCodeTelegram.exe");
                if(!File.Exists(installedExe)) throw new FileNotFoundException("Installer payload is missing the desktop app executable.",installedExe);
                string uninstaller=Path.Combine(dest,"Uninstall.exe");
                File.Copy(installedExe,uninstaller,true);
                string bin=Path.Combine(dest,"bin");Directory.CreateDirectory(bin);
                string tgExecutable=Path.Combine(dest,"dependencies","tg","tg.exe");
                if(!File.Exists(tgExecutable)) throw new FileNotFoundException("Installer payload is missing Telegram CLI.",tgExecutable);
                File.Copy(tgExecutable,Path.Combine(bin,"tg.exe"),true);
                File.WriteAllText(Path.Combine(bin,"opencode-telegram.cmd"),"@echo off\r\n\"%~dp0..\\OpenCodeTelegram.exe\" %*\r\n",new UTF8Encoding(false));
                currentSettings.OpenCodeExecutable=BotRuntime.ResolveOpenCodeExecutable(currentSettings);
                currentSettings.OpenCodeServerVersion="v2";
                SettingsStore.Save(currentSettings);
                BotRuntime.ConfigureOpenCodeCommand(currentSettings);
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
            if(wasBotRunning) { installProgress.IsIndeterminate=true;status.Text=english?"Installation complete. Restarting the Bot service…":"安装完成，正在恢复 Bot 服务…"; await Task.Run(()=>BotRuntime.Start(SettingsStore.Load(),message=>Dispatcher.BeginInvoke(new Action(()=>status.Text=message)))); }
            installProgress.IsIndeterminate=false;installProgress.Value=100;
            ShowCompletionPage();
        }
        catch(Exception ex){status.Text=(english?"Installation failed: ":"安装失败：")+ex.Message;}
        finally{installing=false;installProgress.IsIndeterminate=false;foreach(var control in installerStack.Children.OfType<UIElement>())control.IsEnabled=true;}
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
