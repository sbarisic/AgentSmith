using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentSmith.Core;
using AgentSmith.Runtime;
using AgentSmith.Storage;
using AgentSmith.Tools;
using AgentSmith.LmStudio;
using Xunit;
using static AgentSmith.Tests.RuntimeTests;

namespace AgentSmith.Tests;

public sealed class InspectionToolsTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); return send(request,ct); }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TestDirectory Directory = new();
        public SessionStore Store { get; }
        public ToolRegistry Tools { get; }
        public Session Session { get; }
        public Fixture(HttpMessageHandler? handler = null, HttpMessageHandler? webHandler = null)
        {
            Store = new(Path.Combine(Directory.Path,"data")); Tools = new(Store, web: new WebTools(webHandler), http: new HttpTools(handler));
            Session = new() { Workspace = Directory.Path, Runs = [new() { State = RunState.Completed }] }; Session.Connection.Capabilities.Vision = true;
            Store.Save(Session, new("test",Session.Id,null,new {}));
        }
        public ToolExecutionContext Context(CancellationToken ct = default) => new(Session, Session.Runs.Last(), Store, ct);
        public ToolCall Prepare(string name, object args)
        {
            var call = new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = name, Arguments = Json.Serialize(args) };
            call.Prepared = Tools.Prepare(Context(), call); return call;
        }
        public async Task<JsonNode> Call(string name, object args) => JsonNode.Parse((await Tools.ExecuteAsync(Context(),Prepare(name,args))).Content)!;
        public void Dispose() { Tools.Dispose(); Directory.Dispose(); }
    }
    private static object Request(string method = "GET", string url = "http://127.0.0.1:8080/test", string body = "") => new { method, url, body, headers = Array.Empty<object>(), timeoutSeconds = 0 };
    private static HttpResponseMessage Response(string text, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(text, Encoding.UTF8,"application/json") };
    private static byte[] Png()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6,1)) throw new PlatformNotSupportedException();
        using var bitmap = new System.Drawing.Bitmap(4,3); bitmap.SetPixel(0,0,System.Drawing.Color.Red);
        using var stream = new MemoryStream(); bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
    }
    private static HttpResponseMessage ImageResponse(byte[] bytes, string type = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }
    [Fact]
    public async Task WebImageFollowsPublicRedirectAndReusesSavedSnapshotWithoutDownloadingAgain()
    {
        var bytes = Png();
        var handler = new Handler((request, _) =>
        {
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "image/png");
            if (request.RequestUri!.AbsolutePath == "/original")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://cdn.example.com/image?id=42");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(ImageResponse(bytes, "application/octet-stream"));
        });
        using var f = new Fixture(webHandler: handler);
        var call = f.Prepare("image_read", new { path = "https://example.com/original" });
        var result = await f.Tools.ExecuteAsync(f.Context(), call);
        var attachment = Assert.Single(f.Store.Attachments(f.Session.Id));
        Assert.Null(attachment.SourcePath);
        Assert.Equal("https://example.com/original", attachment.RequestedSourceUrl);
        Assert.Equal("https://cdn.example.com/image?id=42", attachment.SourceUrl);
        Assert.NotNull(attachment.RetrievedAt);
        Assert.Equal(FileOperations.Hash(bytes), attachment.SourceHash);
        Assert.Equal("web-image", JsonNode.Parse(result.Content)!["kind"]!.ToString());
        Assert.DoesNotContain("base64", result.Content);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(result.Content, (await f.Tools.ExecuteAsync(f.Context(), call)).Content);
        var reopened = new SessionStore(f.Store.DataDirectory);
        var saved = reopened.Get(f.Session.Id)!;
        saved.Receipts.Clear(); // Simulate attachment commit before result/receipt persistence.
        var recovered = ImageTool.Recover(new(saved, saved.Runs.Last(), reopened, default), call);
        Assert.Equal(result.Content, recovered.Content);
        Assert.Equal(2, handler.Requests);
        Assert.NotEmpty(AttachmentFiles.Read(reopened, Assert.Single(reopened.Attachments(saved.Id))));
    }
    [Theory]
    [InlineData("http://127.0.0.1/image.png")]
    [InlineData("http://10.0.0.1/image.png")]
    [InlineData("http://[::1]/image.png")]
    [InlineData("https://user:password@example.com/image.png")]
    [InlineData("https://example.com:8080/image.png")]
    [InlineData("https://example.com/image.png?access_token=secret-value")]
    public async Task WebImageRejectsUnsafeInitialAndRedirectDestinations(string url)
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(url); return Task.FromResult(response);
        });
        using var f = new Fixture(webHandler: handler);
        Assert.Throws<UserFacingException>(() => f.Prepare("image_read", new { path = url }));
        Assert.Equal(0, handler.Requests);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Call("image_read", new { path = "https://example.com/start" }));
        Assert.Equal(1, handler.Requests);
        Assert.Empty(f.Store.Attachments(f.Session.Id));
    }
    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public async Task WebImageRejectsUnsupportedTypes(string type)
    {
        var handler = new Handler((_, _) => Task.FromResult(ImageResponse(Png(), type)));
        using var f = new Fixture(webHandler: handler);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Call("image_read", new { path = "https://example.com/image" }));
        Assert.Empty(f.Store.Attachments(f.Session.Id));
    }
    [Fact]
    public async Task WebImageRejectsInvalidBytesAndOversizedDimensionsAndDownloads()
    {
        var hugeDimensions = Png();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hugeDimensions.AsSpan(16, 4), 100000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hugeDimensions.AsSpan(20, 4), 100000);
        foreach (var bytes in new[] { new byte[] { 1, 2, 3 }, hugeDimensions, new byte[AttachmentFiles.MaxBytes + 1] })
        {
            var handler = new Handler((_, _) => Task.FromResult(ImageResponse(bytes)));
            using var f = new Fixture(webHandler: handler);
            await Assert.ThrowsAsync<UserFacingException>(() => f.Call("image_read", new { path = "https://example.com/image" }));
            Assert.Empty(f.Store.Attachments(f.Session.Id));
        }
        // Unknown length exercises the streamed/decompressed size bound, not Content-Length.
        using var web = new WebTools(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new UnseekableImageStream(new byte[AttachmentFiles.MaxBytes + 1])) })));
        await Assert.ThrowsAsync<UserFacingException>(() => web.FetchImageAsync("https://example.com/image", default));
    }
    private sealed class UnseekableImageStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    [Fact]
    public async Task WebImageBecomesActualModelInputAndCredentialUrlsAreNotPersisted()
    {
        var handler = new Handler((_, _) => Task.FromResult(ImageResponse(Png())));
        using var f = new Fixture(webHandler: handler);
        var model = new ScriptedModel([Done(Call("web-image", "image_read", "{\"path\":\"https://example.com/image\"}"))], [Done(Message())]);
        var run = await new AgentRunner(f.Store, model, f.Tools).RunAsync(f.Session, "inspect public image", null, _ => { }, default);
        Assert.Equal(RunState.Completed, run.State);
        var saved = f.Store.Get(f.Session.Id)!;
        Assert.Single(saved.History, i => i["role"]?.ToString() == "user");
        Assert.Contains("input_image", Json.Serialize(model.Requests[1].Input));
        Assert.Contains("data:image/png;base64,", Json.Serialize(AttachmentFiles.Resolve(f.Store, saved, model.Requests[1])));
        Assert.Equal(1, handler.Requests);

        var secretModel = new ScriptedModel([Done(Call("secret", "image_read", "{\"path\":\"https://user:private-password@example.com/image\"}"))], [Done(Message())]);
        await new AgentRunner(f.Store, secretModel, f.Tools).RunAsync(f.Session, "inspect image", null, _ => { }, default);
        Assert.DoesNotContain("private-password", Json.Serialize(f.Store.Get(f.Session.Id)));
        Assert.Equal(1, handler.Requests);
    }
    [Fact]
    public async Task WebImageCancellationAndHttpFailureDoNotImportOrExposeResponseBodies()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("private server diagnostic") }));
        using var f = new Fixture(webHandler: handler);
        var error = await Assert.ThrowsAsync<UserFacingException>(() => f.Call("image_read", new { path = "https://example.com/image" }));
        Assert.Contains("403", error.Message); Assert.DoesNotContain("private server diagnostic", error.Message);
        using var cancellation = new CancellationTokenSource();
        using var web = new WebTools(new Handler(async (_, ct) =>
        { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); return ImageResponse(Png()); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => web.FetchImageAsync("https://example.com/image", cancellation.Token));
        Assert.Empty(f.Store.Attachments(f.Session.Id));
    }
    [Fact]
    public async Task WebImageRedirectLoopsAreBoundedAndActivityOmitsQuery()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("/again", UriKind.Relative); return Task.FromResult(response);
        });
        using var f = new Fixture(webHandler: handler);
        var call = f.Prepare("image_read", new { path = "https://example.com/image?id=private-value" });
        Assert.Equal("https://example.com/image", Activity.Describe(f.Session, call).Target);
        await Assert.ThrowsAsync<UserFacingException>(() => f.Tools.ExecuteAsync(f.Context(), call));
        Assert.Equal(6, handler.Requests);
        Assert.Empty(f.Store.Attachments(f.Session.Id));
        Assert.True(ImageTool.SensitiveArguments("{\"path\":\"https://user:password@example.com/a\"}"));
        Assert.True(ImageTool.SensitiveArguments("{\"path\":\"https://example.com/a?access_token=secret-value\"}"));
    }
    [Fact]
    public async Task ImageEvidencePersistsProjectsAfterAllResultsAndDoesNotBecomeUserHistory()
    {
        using var f = new Fixture(); System.IO.Directory.CreateDirectory(Path.Combine(f.Directory.Path,"artifacts"));
        File.WriteAllBytes(Path.Combine(f.Directory.Path,"artifacts","screen.png"),Png());
        var model = new ScriptedModel([Done(Call("image", "image_read", "{\"path\":\"artifacts/screen.png\"}"), Call("read", "workspace_list", "{\"path\":\".\"}"))], [Done(Message())]);
        var run = await new AgentRunner(f.Store,model,f.Tools).RunAsync(f.Session,"inspect screenshot",null,_=>{},default);
        Assert.Equal(RunState.Completed,run.State);
        var saved = f.Store.Get(f.Session.Id)!; var attachment = Assert.Single(f.Store.Attachments(saved.Id));
        Assert.Equal("image",attachment.SourceToolCallId); Assert.Equal(4,attachment.Width); Assert.Equal(3,attachment.Height);
        Assert.Single(saved.History,i=>i["role"]?.ToString()=="user");
        var input = model.Requests[1].Input;
        Assert.Equal("read", input[^2]["call_id"]?.ToString()); Assert.Equal("input_image",input[^1]["content"]![1]!["type"]!.ToString());
        Assert.DoesNotContain("base64",Json.Serialize(saved));
        var request = AttachmentFiles.Resolve(f.Store,saved,model.Requests[1]);
        var chat = LocalChatModelClient.BuildRequest(request,8192);
        Assert.Contains("data:image/png;base64,",chat.ToJsonString());
        Assert.Contains("data:image/png;base64,",AgentSmith.OpenAI.ChatGptModelClient.BuildRequest(request).ToJsonString());
        Assert.Contains("data:image/png;base64,",LmStudioModelClient.BuildRequest(request,8192).ToJsonString());
        Assert.Contains(attachment.Id,AttachmentFiles.SubmittedIds(saved));
        File.Delete(Path.Combine(f.Directory.Path,"artifacts","screen.png"));
        Assert.Contains("data:image/png;base64,",Json.Serialize(AttachmentFiles.Resolve(f.Store,saved,model.Requests[1])));
    }
    [Theory]
    [InlineData("../image.png")]
    [InlineData(".git/image.png")]
    [InlineData("node_modules/image.png")]
    [InlineData("image.svg")]
    public void ImageRejectsInvalidPaths(string path)
    { using var f = new Fixture(); Assert.ThrowsAny<Exception>(()=>f.Prepare("image_read",new{path})); }
    [Fact]
    public async Task ImageLimitsCapabilitiesAndDuplicateReceipts()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Directory.Path,"image.png"); File.WriteAllBytes(path,Png());
        var call = f.Prepare("image_read",new{path="image.png"});
        var first = await f.Tools.ExecuteAsync(f.Context(),call); var second = await f.Tools.ExecuteAsync(f.Context(),call);
        Assert.Equal(first.Content,second.Content); Assert.Single(f.Store.Attachments(f.Session.Id));
        f.Session.Connection.Capabilities.Vision=false;
        Assert.DoesNotContain(f.Tools.Available(f.Session),d=>d.Name=="image_read");
        Assert.Throws<UserFacingException>(()=>f.Prepare("image_read",new{path="image.png"}));
        Assert.Throws<UserFacingException>(()=>ImageDecoder.Decode([1,2,3]));
        var huge=Png(); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(16,4),100000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(20,4),100000);
        Assert.Throws<UserFacingException>(()=>ImageDecoder.Decode(huge));
    }
    [Fact]
    public async Task MapFindsStaticProjectsWithoutExecutingAndKeepsFullArtifact()
    {
        using var f = new Fixture(); var src=Path.Combine(f.Directory.Path,"src"); System.IO.Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src,"App.csproj"),"<Project><PropertyGroup Condition=\"'$(X)'=='yes'\"><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"Other.csproj\"/><PackageReference Include=\"xunit\" Version=\"2.9.3\"/></ItemGroup><Target Name=\"Evil\"><Exec Command=\"never run\"/></Target></Project>");
        File.WriteAllText(Path.Combine(src,"Program.cs"),"// entry candidate"); File.WriteAllText(Path.Combine(f.Directory.Path,"README.md"),"docs");
        System.IO.Directory.CreateDirectory(Path.Combine(f.Directory.Path,"bin"));File.WriteAllText(Path.Combine(f.Directory.Path,"bin","hidden.cs"),"hidden");
        var result=await f.Call("workspace_map",new{path="src",depth=3,maxEntries=1});
        Assert.Single(result["entries"]!.AsArray()); Assert.Contains("net10.0",result.ToJsonString()); Assert.Contains("xunit",result.ToJsonString());
        var artifact=Assert.Single(f.Session.Artifacts); var text=File.ReadAllText(artifact.Path);
        Assert.Contains("Program.cs",text); Assert.Contains("condition",text); Assert.DoesNotContain("hidden.cs",text);
        Assert.Empty(f.Session.Processes);
        var response = await f.Call("artifact_read", new { artifactId=artifact.Id,cursor=0,maxCharacters=4000 }); Assert.Contains("net10.0",response.ToJsonString());
    }
    [Fact]
    public async Task MapRejectsXmlEntitiesAndReportsDepthLimits()
    {
        using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Directory.Path,"bad.csproj"),"<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/secret'>]><Project>&e;</Project>");
        System.IO.Directory.CreateDirectory(Path.Combine(f.Directory.Path,"deep","nested"));
        var result=await f.Call("workspace_map",new{path=".",depth=1,maxEntries=10});
        Assert.True(result["depthLimited"]!.GetValue<bool>()); Assert.NotEmpty(result["issues"]!.AsArray());
        Assert.Throws<UserFacingException>(()=>f.Prepare("workspace_map",new{path="..",depth=1,maxEntries=10}));
    }
    [Theory]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://router.local/")]
    [InlineData("file:///C:/secret")]
    [InlineData("https://user:password@example.com/")]
    public void HttpRejectsUnsupportedDestinations(string url) => Assert.Throws<UserFacingException>(()=>HttpTools.ValidateUrl(url));
    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://localhost:1234/")]
    [InlineData("http://[::1]:4567/")]
    [InlineData("https://example.com:8443/")]
    public void HttpAllowsLoopbackAndPublic(string url) => Assert.NotNull(HttpTools.ValidateUrl(url));
    [Fact]
    public void DnsAddressChecksDoNotLetPublicHostsResolveToLocalServices()
    {
        Assert.False(HttpTools.AllowedAddress(IPAddress.Loopback,false)); Assert.True(HttpTools.AllowedAddress(IPAddress.Loopback,true));
        Assert.False(HttpTools.AllowedAddress(IPAddress.Parse("10.0.0.1"),true)); Assert.False(HttpTools.AllowedAddress(IPAddress.Parse("8.8.8.8"),true));
    }
    [Fact]
    public async Task HttpPermissionMatrixAndSafeActivity()
    {
        using var f=new Fixture(new Handler((_,_)=>Task.FromResult(Response("{}"))));
        foreach(var workflow in Enum.GetValues<WorkflowMode>()) foreach(var mode in Enum.GetValues<PermissionMode>()) foreach(var child in new[]{false,true})
        {
            f.Session.Workflow=workflow;f.Session.Mode=mode;f.Session.ParentConversationId=child?"parent":null;
            await f.Call("http_request",Request());
            var writable=!child && mode==PermissionMode.Execute && workflow is WorkflowMode.Work or WorkflowMode.Document;
            var definition=Assert.Single(f.Tools.Available(f.Session),d=>d.Name=="http_request");
            Assert.Equal(writable,definition.Parameters["properties"]!["method"]!["enum"]!.AsArray().Any(n=>n!.ToString()=="POST"));
            if(writable) await f.Call("http_request",Request("POST",body:"{}"));
            else Assert.Throws<UserFacingException>(()=>f.Prepare("http_request",Request("POST",body:"{}")));
        }
        var call=new ToolCall{Name="http_request",Arguments=Json.Serialize(Request(url:"https://example.com/api?private=hidden"))};
        Assert.DoesNotContain("hidden",Json.Serialize(AgentSmith.Core.Activity.Describe(f.Session,call)));
    }
    [Fact]
    public async Task HttpErrorResponsesRedirectsLimitsAndReceipts()
    {
        var handler=new Handler((_,_)=>{var r=Response(new string('x',HttpTools.MaxResponseBytes+50),HttpStatusCode.BadRequest);r.Headers.TryAddWithoutValidation("Set-Cookie","private=secret");return Task.FromResult(r);});
        using var f=new Fixture(handler); var call=f.Prepare("http_request",Request("POST",body:"{}"));
        var first=await f.Tools.ExecuteAsync(f.Context(),call);var data=JsonNode.Parse(first.Content)!;
        Assert.Equal(400,data["status"]!.GetValue<int>());Assert.True(data["truncated"]!.GetValue<bool>());
        Assert.True(Encoding.UTF8.GetByteCount(data["excerpt"]!.ToString())<=8192);Assert.DoesNotContain("private=secret",first.Content);
        var restored=f.Store.Get(f.Session.Id)!;var recovered=f.Tools.Recover(new(restored,restored.Runs.Last(),f.Store,default),call);
        Assert.Equal(first.Content,recovered!.Content); await f.Tools.ExecuteAsync(f.Context(),call); Assert.Equal(1,handler.Requests);
        Assert.True(new FileInfo(Assert.Single(f.Session.Artifacts).Path).Length<=HttpTools.MaxResponseBytes);
    }
    [Fact]
    public async Task HttpUncertainWritesAreNeverReplayed()
    {
        var handler=new Handler((_,_)=>throw new HttpRequestException("sensitive transport detail")); using var f=new Fixture(handler);
        var model=new ScriptedModel([Done(Call("write","http_request",Json.Serialize(Request("POST",body:"{}"))))]);
        var run=await new AgentRunner(f.Store,model,f.Tools).RunAsync(f.Session,"test API",null,_=>{},default);
        Assert.Equal(RunState.RecoveryRequired,run.State);Assert.Equal(CallState.Started,Assert.Single(run.Calls).State);
        var saved=f.Store.Get(f.Session.Id)!;
        await new AgentRunner(f.Store,new ScriptedModel(),f.Tools).RunAsync(saved,null,null,_=>{},default);
        Assert.Equal(1,handler.Requests);Assert.DoesNotContain("sensitive transport detail",Json.Serialize(saved));
    }
    [Fact]
    public async Task HttpCredentialsAreRemovedBeforePersistence()
    {
        using var f=new Fixture();var args=Json.Serialize(new{method="GET",url="https://example.com",body="",timeoutSeconds=0,headers=new[]{new{name="Authorization",value="private-secret-value"}}});
        var model=new ScriptedModel([Done(Call("secret","http_request",args))],[Done(Message())]);
        await new AgentRunner(f.Store,model,f.Tools).RunAsync(f.Session,"test",null,_=>{},default);
        Assert.DoesNotContain("private-secret-value",Json.Serialize(f.Store.Get(f.Session.Id)));
    }
    [Fact]
    public async Task CancelledHttpWriteRetainsUncertainty()
    {
        using var cancellation = new CancellationTokenSource();
        using var f = new Fixture(new Handler((_,_) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }));
        var model = new ScriptedModel([Done(Call("cancel-write", "http_request", Json.Serialize(Request("POST",body:"{}"))))]);
        var run = await new AgentRunner(f.Store,model,f.Tools).RunAsync(f.Session,"test",null,_=>{},cancellation.Token);
        Assert.Equal(RunState.RecoveryRequired,run.State); Assert.Equal(CallState.Started,Assert.Single(run.Calls).State);
    }
    [Fact]
    public void ImportedImageRecoveryUsesOriginalBytesAfterSourceDeletion()
    {
        using var f = new Fixture(); var source = Path.Combine(f.Directory.Path,"image.png"); File.WriteAllBytes(source,Png());
        var call = f.Prepare("image_read", new { path="image.png" });
        var decoded = ImageDecoder.Decode(Png());
        var original = AttachmentFiles.Import(f.Store,f.Session.Id,decoded.Bytes,decoded.Width,decoded.Height,sourcePath:"image.png",sourceRunId:f.Session.Runs.Last().Id,sourceToolCallId:call.Id);
        File.Delete(source);
        var result = ImageTool.Recover(f.Context(),call); Assert.Contains(original.Id,result.Content);Assert.Single(f.Store.Attachments(f.Session.Id));
        var fresh = new ToolCall { Id="never-imported",Name="image_read",Arguments=call.Arguments };
        Assert.Contains("No committed",ImageTool.Recover(f.Context(),fresh).Content);Assert.Single(f.Store.Attachments(f.Session.Id));
    }
    [Fact]
    public async Task RealLoopbackPostPreservesBodyAndReturnsObservedError()
    {
        using var listener = new TcpListener(IPAddress.Loopback,0); listener.Start(); var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client=await listener.AcceptTcpClientAsync(timeout.Token);using var stream=client.GetStream();
            using var reader=new StreamReader(stream,Encoding.UTF8,false,1024,true);
            Assert.StartsWith("POST /echo HTTP/1.1",await reader.ReadLineAsync(timeout.Token));
            var length=0;
            while(await reader.ReadLineAsync(timeout.Token) is {Length:>0} line)
            {
                Assert.DoesNotContain("Authorization:",line,StringComparison.OrdinalIgnoreCase);
                if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) length=int.Parse(line[15..]);
            }
            var chars=new char[length];Assert.Equal(length,await reader.ReadBlockAsync(chars,timeout.Token));Assert.Equal("{\"name\":\"AgentSmith\"}",new string(chars));
            var body="{\"received\":true,\"stored\":false}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 409 Conflict\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"),timeout.Token);
        });
        using var f=new Fixture(); var result=await f.Call("http_request",new{method="POST",url=$"http://127.0.0.1:{port}/echo",body="{\"name\":\"AgentSmith\"}",headers=new[]{new{name="Content-Type",value="application/json"}},timeoutSeconds=10});
        Assert.Equal(409,result["status"]!.GetValue<int>());Assert.Contains("received",result["excerpt"]!.ToString());await server;
    }
    [Fact]
    public async Task RealLoopbackTransportReturnsRedirectWithoutFollowingIt()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server=Task.Run(async()=>{using var socket=await listener.AcceptTcpClientAsync(limit.Token);using var stream=socket.GetStream();var buffer=new byte[8192];var count = await stream.ReadAsync(buffer,limit.Token); Assert.True(count > 0);await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://10.0.0.1/private\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),limit.Token);});
        using var f=new Fixture();var result=await f.Call("http_request",Request(url:$"http://127.0.0.1:{port}/"));
        Assert.Equal(302,result["status"]!.GetValue<int>());await server;
    }
}
