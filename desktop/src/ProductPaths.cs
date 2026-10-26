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

static class ProductPaths
{
    public const string AppName = "opencode_telegram_bot";
    public static string Version { get { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }
    public const string EnvRoot = "OPENCODE_TELEGRAM_ROOT";
    public const string EnvHome = "OPENCODE_TELEGRAM_HOME";
    public static string Root { get { return Environment.GetEnvironmentVariable(EnvRoot) ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); } }
    public static string Home { get { return Environment.GetEnvironmentVariable(EnvHome) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenCodeTelegram"); } }
    public static string SettingsFile { get { return Path.Combine(Home, "desktop-settings.dpapi"); } }
    public static string BotCli { get { return Path.Combine(Root, "bot", "dist", "cli.js"); } }
    public static string Node { get { return BotRuntime.ResolveNodeExecutable(); } }
    public static string NpmCli { get { return Path.Combine(Root, "runtime", "node_modules", "npm", "bin", "npm-cli.js"); } }
    public static string BotModulesBin { get { return Path.Combine(Root, "bot", "node_modules", ".bin"); } }
}
