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
