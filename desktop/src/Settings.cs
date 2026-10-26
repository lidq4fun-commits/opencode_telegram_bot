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


[DataContract]
sealed class DesktopSettings
{
    public DesktopSettings()
    {
        BotToken = ""; AllowedUserId = ""; Locale = "zh"; Eot10Password = "";
        Eot10Enabled = false;
        OpenCodeUrl = "http://127.0.0.1:49374"; OpenCodeServerVersion = "v2"; OpenCodeUsername = "opencode";
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
    [DataMember] public bool SetupCompleted { get; set; }
    [DataMember] public int SetupStep { get; set; }
    [DataMember] public string TelegramUserId { get; set; }
    public string ResolvedWebFollowMode { get { return "force"; } }
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
            try { using (var stream = new MemoryStream(clear)) { var settings=(DesktopSettings)new DataContractJsonSerializer(typeof(DesktopSettings)).ReadObject(stream); if(settings.OpenCodeServerVersion!="v2") { settings.OpenCodeServerVersion="v2"; settings.OpenCodeExecutable=""; if(settings.OpenCodeUrl=="http://127.0.0.1:4096")settings.OpenCodeUrl="http://127.0.0.1:49374"; } return settings; } }
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
            key.SetValue("DisplayVersion", ProductPaths.Version, RegistryValueKind.String);
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
