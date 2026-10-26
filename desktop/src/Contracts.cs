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
sealed class TelegramIdentityResult
{
    [DataMember(Name="data")] public TelegramIdentity Data { get; set; }
}
[DataContract]
sealed class TelegramIdentity
{
    [DataMember(Name="id")] public long Id { get; set; }
    [DataMember(Name="bot")] public bool Bot { get; set; }
}

[DataContract]
sealed class TelegramWebContext
{
    [DataMember(Name="currentProject")] public TelegramWebProject Project { get; set; }
    [DataMember(Name="currentSession")] public TelegramWebSession Session { get; set; }
    [DataMember(Name="instance")] public string Instance { get; set; }
    [DataMember(Name="mobileRevision")] public long MobileRevision { get; set; }
    [DataMember(Name="mobileActive")] public bool MobileActive { get; set; }
    [DataMember(Name="busy")] public bool Busy { get; set; }
    [DataMember(Name="updatedAt")] public long UpdatedAt { get; set; }

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
        string key=authority;
        string encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+','-').Replace('/','_');
        string path="/server/"+encoded;
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
    public static bool TryLoadSync(string home,out TelegramWebContext context,out string json)
    {
        context=null;json=null;
        try
        {
            using(var stream=new FileStream(Path.Combine(home,"desktop-sync","state.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new StreamReader(stream,Encoding.UTF8))json=reader.ReadToEnd();
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(json)))context=(TelegramWebContext)new DataContractJsonSerializer(typeof(TelegramWebContext)).ReadObject(stream);
            return context!=null&&!String.IsNullOrWhiteSpace(context.Instance)&&DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-context.UpdatedAt<6000;
        }
        catch(IOException){}catch(UnauthorizedAccessException){}catch(SerializationException){}
        return false;
    }
}
// Written by the Bot daemon at start; reading it here keeps `status` out of a Node spawn.
[DataContract]
sealed class BotServiceState
{
    [DataMember(Name="pid")] public int Pid { get; set; }
    [DataMember(Name="startedAt")] public string StartedAt { get; set; }
    [DataMember(Name="logFilePath")] public string LogFilePath { get; set; }
    [DataMember(Name="mode")] public string Mode { get; set; }
}

// OpenCode V2 service manager state; `pid`/`url` describe the service that is running now.
[DataContract]
sealed class OpenCodeServiceState
{
    [DataMember(Name="url")] public string Url { get; set; }
    [DataMember(Name="pid")] public int Pid { get; set; }
    [DataMember(Name="version")] public string Version { get; set; }
    [DataMember(Name="password")] public string Password { get; set; }
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
