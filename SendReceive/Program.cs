using Bandwidth.Standard.Api;
using Bandwidth.Standard.Client;
using Bandwidth.Standard.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

//Setting up environment variables
string? BW_ACCOUNT_ID = Environment.GetEnvironmentVariable("BW_ACCOUNT_ID");
string? BW_CLIENT_ID = Environment.GetEnvironmentVariable("BW_CLIENT_ID");
string? BW_CLIENT_SECRET = Environment.GetEnvironmentVariable("BW_CLIENT_SECRET");
string? BW_MESSAGING_APPLICATION_ID = Environment.GetEnvironmentVariable("BW_MESSAGING_APPLICATION_ID");
string? BW_NUMBER = Environment.GetEnvironmentVariable("BW_NUMBER");
string? LOCAL_PORT = Environment.GetEnvironmentVariable("LOCAL_PORT");

if (BW_ACCOUNT_ID == null || BW_CLIENT_ID == null || BW_CLIENT_SECRET == null || BW_MESSAGING_APPLICATION_ID == null
    || BW_NUMBER == null || !int.TryParse(LOCAL_PORT, out int port))
{
    Console.WriteLine("Please set the environmental variables defined in the README");
    Environment.Exit(1);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");
var app = builder.Build();

Configuration configuration = new Configuration();
configuration.OAuthClientId = BW_CLIENT_ID;
configuration.OAuthClientSecret = BW_CLIENT_SECRET;

var statusMessages = new Dictionary<StatusCallbackTypeEnum, string>
{
    [StatusCallbackTypeEnum.Sending] = "message-sending type is only for MMS.",
    [StatusCallbackTypeEnum.Delivered] = "Your message has been handed off to the Bandwidth's MMSC network, but has not been confirmed at the downstream carrier.",
    [StatusCallbackTypeEnum.Failed] = "For MMS and Group Messages, you will only receive this callback if you have enabled delivery receipts on MMS."
};

async Task<T?> ReadBody<T>(HttpContext context)
{
    try
    {
        using var streamReader = new StreamReader(context.Request.Body);
        return JsonConvert.DeserializeObject<T>(await streamReader.ReadToEndAsync());
    }
    catch (JsonException)
    {
        return default;
    }
}

async Task SaveMedia(string mediaUrl)
{
    var mediaId = mediaUrl.Substring(mediaUrl.IndexOf("media/") + "media/".Length);
    var mediaName = Path.GetFileName(mediaId);
    if (mediaName.EndsWith(".xml"))
    {
        return;
    }

    try
    {
        MediaApi mediaApi = new MediaApi(configuration);
        using var media = await mediaApi.GetMediaAsync(BW_ACCOUNT_ID, mediaId);
        Directory.CreateDirectory("media");
        using var fileStream = File.Create(Path.Combine("media", mediaName));
        await media.CopyToAsync(fileStream);
        Console.WriteLine($"Saved media to media/{mediaName}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"Failed to download {mediaUrl}: {e.Message}");
    }
}

app.MapPost("/messages", async (HttpContext context) =>
{
    var requestBody = await ReadBody<JObject>(context);
    var media = requestBody?["media"] ?? new JArray();
    if (requestBody?["to"]?.Type != JTokenType.String || requestBody["text"]?.Type != JTokenType.String
        || media is not JArray mediaList || mediaList.Any(url => url.Type != JTokenType.String))
    {
        return Results.BadRequest();
    }

    MessageRequest messageRequest = new MessageRequest(
        applicationId: BW_MESSAGING_APPLICATION_ID,
        to: new List<string> { requestBody["to"]!.ToString() },
        from: BW_NUMBER,
        text: requestBody["text"]!.ToString(),
        media: mediaList.Count > 0 ? mediaList.Select(url => url.ToString()).ToList() : null
    );

    try
    {
        MessagesApi apiInstance = new MessagesApi(configuration);
        Message message = await apiInstance.CreateMessageAsync(BW_ACCOUNT_ID, messageRequest);
        Console.WriteLine($"Sent message {message.Id}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"Failed to send message: {e.Message}");
        return Results.StatusCode(500);
    }

    return Results.NoContent();
});

app.MapPost("/callbacks/outbound/messaging/status", async (HttpContext context) =>
{
    var callbacks = await ReadBody<List<StatusCallback>>(context);
    if (callbacks == null || !callbacks.All(callback => statusMessages.ContainsKey(callback.Type)))
    {
        return Results.BadRequest();
    }

    foreach (var callback in callbacks)
    {
        Console.WriteLine(statusMessages[callback.Type]);
    }

    return Results.NoContent();
});

app.MapPost("/callbacks/inbound/messaging", async (HttpContext context) =>
{
    var callbacks = await ReadBody<List<InboundCallback>>(context);
    if (callbacks == null || !callbacks.All(callback => callback.Type == InboundCallbackTypeEnum.MessageReceived))
    {
        return Results.BadRequest();
    }

    foreach (var callback in callbacks)
    {
        var message = callback.Message;
        Console.WriteLine($"{callback.Description}\nTo: {message.To[0]}\nFrom: {message.From}\nText: {message.Text}");
        foreach (var mediaUrl in message.Media ?? new List<string>())
        {
            await SaveMedia(mediaUrl);
        }
    }

    return Results.NoContent();
});

app.Run();
