using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static class XToken
{
    static WebProxy proxy = new();
    static HttpClientHandler handler = new() { Proxy = proxy };
    static HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    static HttpClient proxyListClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
    static string PROXY_LIST_URL = Environment.GetEnvironmentVariable("PROXY_LIST_URL")!;
    static string AUTHORIZATION = Environment.GetEnvironmentVariable("AUTHORIZATION")!;
    const string GUEST_TOKEN_URL = "https://api.twitter.com/1.1/guest/activate.json";
    const string FLOW_TOKEN_URL = "https://api.twitter.com/1.1/onboarding/task.json?flow_name=welcome";
    const string OAUTH_TOKEN_URL = "https://api.twitter.com/1.1/onboarding/task.json";
    const string CONTENT_TYPE = "application/json";
    const string USER_AGENT = "TwitterAndroid/10.10.0";

    static async Task<string[]> getProxyListAsync()
    {
        var response = await proxyListClient.GetAsync(PROXY_LIST_URL);
        var content = await response.Content.ReadAsStringAsync();
        return content.Split('\n');
    }

    static async Task<string?> getGuestToken(string proxy, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GUEST_TOKEN_URL);
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        try
        {
            XToken.proxy.Address = new Uri($"socks5://{proxy}");
            var response = await client.SendAsync(request, token);
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine(content);
            var json = JsonNode.Parse(content);
            return json?["guest_token"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    static async Task<string?> getFlowToken(string proxy, CancellationToken ct, string guestToken)
    {
        string data = "{\"flow_token\":null,\"input_flow_data\":{\"flow_context\":{\"start_location\":{\"location\":\"splash_screen\"}}}}";
        Console.WriteLine(data);
        var request = new HttpRequestMessage(HttpMethod.Post, FLOW_TOKEN_URL);
        request.Content = new StringContent(data, Encoding.UTF8, CONTENT_TYPE);
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        request.Headers.UserAgent.ParseAdd(USER_AGENT);
        request.Headers.Add("X-Guest-Token", guestToken);

        try
        {
            XToken.proxy.Address = new Uri($"socks5://{proxy}");
            var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine(content);
            var json = JsonNode.Parse(content);
            return json?["flow_token"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    static async Task<(string, string)?> getOAuthToken(string proxy, CancellationToken ct, string guestToken, string flowToken)
    {
        string data = $"{{\"flow_token\":\"{flowToken}\",\"subtask_inputs\":[{{\"open_link\":{{\"link\":\"next_link\"}},\"subtask_id\":\"NextTaskOpenLink\"}}]}}";
        var request = new HttpRequestMessage(HttpMethod.Post, OAUTH_TOKEN_URL);
        request.Content = new StringContent(data, Encoding.UTF8, CONTENT_TYPE);
        request.Headers.Authorization = new("Bearer", AUTHORIZATION);
        request.Headers.UserAgent.ParseAdd(USER_AGENT);
        request.Headers.Add("X-Guest-Token", guestToken);

        try
        {
            XToken.proxy.Address = new Uri($"socks5://{proxy}");
            var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync();
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

    static async Task<string> getResponseText()
    {
        Console.WriteLine("Getting proxy list...");
        string[] proxyList = await getProxyListAsync();
        Console.WriteLine($"Got {proxyList.Length} proxies");
        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        var tasks = new List<Task<string>>();
        foreach (var proxy in proxyList)
        {
            var task = async () =>
            {
                Console.WriteLine($"Trying {proxy}");

                var guestToken = await getGuestToken(proxy, ct);
                if (guestToken == null)
                    return "";
                Console.WriteLine($"Got guest token: {guestToken}");

                var flowToken = await getFlowToken(proxy, ct, guestToken);
                if (flowToken == null)
                    return "";
                Console.WriteLine($"Got flow token: {flowToken}");

                var oauthToken = await getOAuthToken(proxy, ct, guestToken, flowToken);
                if (oauthToken == null)
                    return "";
                cts.Cancel();
                Console.WriteLine($"Got oauth token: {oauthToken}");

                var (token, secret) = oauthToken.Value;
                return $"{token},{secret}";
            };

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
                    var text = getResponseText().Result;
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
