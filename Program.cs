using System.Net;
using System.Text;
using System.Text.Json.Nodes;

internal static class XToken
{
    private static readonly WebProxy proxy = new();
    private static readonly HttpClientHandler handler = new() { Proxy = proxy };
    private static readonly HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(60) };
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

    private static async Task<string?> GetGuestToken(string proxy, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GUEST_TOKEN_URL);
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        try
        {
            XToken.proxy.Address = new Uri($"socks5://{proxy}");
            var response = await client.SendAsync(request, token);
            var content = await response.Content.ReadAsStringAsync(token);
            Console.WriteLine(content);
            var json = JsonNode.Parse(content);
            return json?["guest_token"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetFlowToken(string proxy, string guestToken, CancellationToken ct)
    {
        string data = "{\"flow_token\":null,\"input_flow_data\":{\"flow_context\":{\"start_location\":{\"location\":\"splash_screen\"}}}}";
        Console.WriteLine(data);
        var request = new HttpRequestMessage(HttpMethod.Post, FLOW_TOKEN_URL)
        {
            Content = new StringContent(data, Encoding.UTF8, CONTENT_TYPE)
        };
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        request.Headers.UserAgent.ParseAdd(USER_AGENT);
        request.Headers.Add("X-Guest-Token", guestToken);

        try
        {
            XToken.proxy.Address = new Uri($"socks5://{proxy}");
            var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine(content);
            var json = JsonNode.Parse(content);
            return json?["flow_token"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string, string)?> GetOAuthToken(string proxy, string guestToken, string flowToken, CancellationToken ct)
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
            XToken.proxy.Address = new Uri($"socks5://{proxy}");
            var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine(content);
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
        Console.WriteLine("Getting proxy list...");
        string[] proxyList = await GetProxyListAsync();
        Console.WriteLine($"Got {proxyList.Length} proxies");
        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        var tasks = new List<Task<string>>();
        foreach (var proxy in proxyList)
        {
            async Task<string> task()
            {
                Console.WriteLine($"Trying {proxy}");

                var guestToken = await GetGuestToken(proxy, ct);
                if (guestToken == null)
                    return "";
                Console.WriteLine($"Got guest token: {guestToken}");

                var flowToken = await GetFlowToken(proxy, guestToken, ct);
                if (flowToken == null)
                    return "";
                Console.WriteLine($"Got flow token: {flowToken}");

                var oauthToken = await GetOAuthToken(proxy, guestToken, flowToken, ct);
                if (oauthToken == null)
                    return "";
                cts.Cancel();
                Console.WriteLine($"Got oauth token: {oauthToken}");

                var (token, secret) = oauthToken.Value;
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

    public static void Main()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://*:80/");
        listener.Start();
        Console.WriteLine("Listening...");
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
                    var text = GetResponseText().Result;
                    Console.WriteLine($"Response: {text}");
                    context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(text));
                    context.Response.OutputStream.Close();
                    break;
                default:
                    context.Response.StatusCode = 404;
                    context.Response.OutputStream.Close();
                    break;
            }
        }
    }
}
