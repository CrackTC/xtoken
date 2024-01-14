using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

internal static class XToken
{
    private static readonly HttpClient proxyListClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string PROXY_LIST_URL = Environment.GetEnvironmentVariable("PROXY_LIST_URL")!;
    private static readonly string AUTHORIZATION = Environment.GetEnvironmentVariable("AUTHORIZATION")
        ?? "AAAAAAAAAAAAAAAAAAAAAFXzAwAAAAAAMHCxpeSDG1gLNLghVe8d74hl6k4%3DRUMF4xAQLsbeBhTSRrCiQpJtxoGWeyHrDb5te2jpGskWDFW82F";
    private const string GUEST_TOKEN_URL = "https://api.twitter.com/1.1/guest/activate.json";
    private const string FLOW_TOKEN_URL = "https://api.twitter.com/1.1/onboarding/task.json?flow_name=welcome";
    private const string OAUTH_TOKEN_URL = "https://api.twitter.com/1.1/onboarding/task.json";
    private const string CONTENT_TYPE = "application/json";
    private const string USER_AGENT = "TwitterAndroid/10.10.0";

    private static async Task<string[]> GetProxyListAsync()
    {
        var response = await proxyListClient.GetAsync(PROXY_LIST_URL);
        var content = await response.Content.ReadAsStringAsync();
        return content.Split('\n');
    }

    private static async Task<string?> GetGuestToken(HttpClient client, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GUEST_TOKEN_URL);
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        try
        {
            var response = await client.SendAsync(request, token);
            var content = await response.Content.ReadAsStringAsync(token);
            Console.Error.WriteLine(content);
            var json = JsonNode.Parse(content);
            return json?["guest_token"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetFlowToken(HttpClient client, string guestToken, CancellationToken ct)
    {
        string data = "{\"flow_token\":null,\"input_flow_data\":{\"flow_context\":{\"start_location\":{\"location\":\"splash_screen\"}}}}";
        Console.Error.WriteLine(data);
        var request = new HttpRequestMessage(HttpMethod.Post, FLOW_TOKEN_URL)
        {
            Content = new StringContent(data, Encoding.UTF8, CONTENT_TYPE)
        };
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        request.Headers.UserAgent.ParseAdd(USER_AGENT);
        request.Headers.Add("X-Guest-Token", guestToken);

        try
        {
            var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            Console.Error.WriteLine(content);
            var json = JsonNode.Parse(content);
            return json?["flow_token"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string, string)?> GetOAuthToken(HttpClient client, string guestToken, string flowToken, CancellationToken ct)
    {
        string data = $"{{\"flow_token\":\"{flowToken}\",\"subtask_inputs\":[{{\"open_link\":{{\"link\":\"next_link\"}},\"subtask_id\":\"NextTaskOpenLink\"}}]}}";
        var request = new HttpRequestMessage(HttpMethod.Post, OAUTH_TOKEN_URL)
        {
            Content = new StringContent(data, Encoding.UTF8, CONTENT_TYPE)
        };
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        request.Headers.UserAgent.ParseAdd(USER_AGENT);
        request.Headers.Add("X-Guest-Token", guestToken);

        try
        {
            var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            Console.Error.WriteLine(content);
            var json = JsonNode.Parse(content);
            var openAccount = json?["subtasks"]?[0]?["open_account"];
            var oauthToken = openAccount?["oauth_token"]?.GetValue<string>();
            var oauthTokenSecret = openAccount?["oauth_token_secret"]?.GetValue<string>();
            return oauthToken != null && oauthTokenSecret != null
                ? (oauthToken, oauthTokenSecret)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> GetResponseText()
    {
        Console.Error.WriteLine("Getting proxy list...");
        string[] proxyList = await GetProxyListAsync();
        Console.Error.WriteLine($"Got {proxyList.Length} proxies");
        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        var semaphore = new SemaphoreSlim(300);

        var tasks = new List<Task<string>>();
        foreach (var proxy in proxyList)
        {
            async Task<string> task()
            {
                try
                {
                    semaphore.Wait(ct);
                }
                catch (OperationCanceledException)
                {
                    return "";
                }

                Console.Error.WriteLine($"Trying {proxy}");
                var clientHandler = new HttpClientHandler { Proxy = new WebProxy($"socks5://{proxy}") };
                var client = new HttpClient(clientHandler, true) { Timeout = TimeSpan.FromSeconds(3) };

                var guestToken = await GetGuestToken(client, ct);
                if (guestToken == null)
                {
                    semaphore.Release();
                    return "";
                }
                Console.Error.WriteLine($"Got guest token: {guestToken}");

                var flowToken = await GetFlowToken(client, guestToken, ct);
                if (flowToken == null)
                {
                    semaphore.Release();
                    return "";
                }
                Console.Error.WriteLine($"Got flow token: {flowToken}");

                var oauthToken = await GetOAuthToken(client, guestToken, flowToken, ct);
                if (oauthToken == null)
                {
                    semaphore.Release();
                    return "";
                }
                cts.Cancel();
                Console.Error.WriteLine($"Got oauth token: {oauthToken}");

                var (token, secret) = oauthToken.Value;
                semaphore.Release();
                return $"{token},{secret}";
            }

            tasks.Add(task());
        }

        Task.WaitAll(tasks.ToArray());

        foreach (var task in tasks)
        {
            var result = task.Result;
            if (result != "")
                return result;
        }

        return "";
    }

    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "get")
        {
            Console.WriteLine(GetResponseText().Result);
            return;
        }

        var listener = new HttpListener();
        listener.Prefixes.Add("http://*:80/");
        listener.Start();
        Console.Error.WriteLine("Listening...");
        while (true)
        {
            var context = listener.GetContext();
            switch (context.Request.Url?.AbsolutePath)
            {
                case "/":
                    var token = context.Request.QueryString["token"] ?? "";
                    if (token != (Environment.GetEnvironmentVariable("TOKEN") ?? ""))
                    {
                        context.Response.StatusCode = 403;
                        context.Response.OutputStream.Close();
                        break;
                    }

                    {
                        string text;
                        using var process = Process.Start(new ProcessStartInfo
                        {
                            FileName = "./xtoken",
                            Arguments = "get",
                            RedirectStandardOutput = true,
                            UseShellExecute = false
                        });
                        text = process?.StandardOutput.ReadToEnd() ?? "";
                        Console.Error.WriteLine($"Response: {text}");
                        context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(text));
                        context.Response.OutputStream.Close();
                    }
                    break;
                default:
                    context.Response.StatusCode = 404;
                    context.Response.OutputStream.Close();
                    break;
            }
        }
    }
}
