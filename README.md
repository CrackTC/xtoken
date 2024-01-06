
# xtoken

Generate twitter oauth token and secret using socks5 proxy list.

## Deploy

Using docker compose

```yaml
services:
  xtoken:
    image: cracktc/xtoken
    container_name: xtoken
    restart: always
    ports:
      - 8080:80
    environment:
      - TOKEN=<token>
      - PROXY_LIST_URL=<url to socks5 proxy list>
      - AUTHORIZATION=<authorization header>
```

## Usage

Simply send a GET request to `/` with `token=<token>` url parameter.

The response is in `<oauth_token>,<oauth_token_secret>` format.

## Example

Here is an example of [RSSHub](https://github.com/DIYgod/RSSHub) twitter oauth token refresh, which needs some modification to RSSHub's source code.

```diff
diff --git a/lib/config.js b/lib/config.js
index a786fd58d..e3e303ca2 100644
--- a/lib/config.js
+++ b/lib/config.js
@@ -295,6 +295,7 @@ const calculateValue = () => {
         twitter: {
             oauthTokens: envs.TWITTER_OAUTH_TOKEN?.split(','),
             oauthTokenSecrets: envs.TWITTER_OAUTH_TOKEN_SECRET?.split(','),
+            oauthTokenFetchURL: envs.TWITTER_OAUTH_TOKEN_FETCH_URL,
         },
         weibo: {
             app_key: envs.WEIBO_APP_KEY,
diff --git a/lib/v2/twitter/web-api/twitter-api.js b/lib/v2/twitter/web-api/twitter-api.js
index 9d7e75fd1..f46596f0b 100644
--- a/lib/v2/twitter/web-api/twitter-api.js
+++ b/lib/v2/twitter/web-api/twitter-api.js
@@ -7,12 +7,33 @@ const CryptoJS = require('crypto-js');
 const queryString = require('query-string');
 
 let tokenIndex = 0;
+let lastRequestSuccess = true;
+let lastFetchTime = 0;
+
+const fetchOAuthToken = async () => {
+    const response = await got(config.twitter.oauthTokenFetchURL).text();
+    const [token, tokenSecret] = response.split(',');
+    console.log('Fetched twitter oauth token: ', token, tokenSecret)
+    config.twitter.oauthTokens = [token];
+    config.twitter.oauthTokenSecrets = [tokenSecret];
+}
 
 const twitterGot = async (url, params) => {
     if (!config.twitter.oauthTokens?.length || !config.twitter.oauthTokenSecrets?.length || config.twitter.oauthTokens.length !== config.twitter.oauthTokenSecrets.length) {
+        if (Date.now() - lastFetchTime > 1000 * 60 * 60) {
+            lastFetchTime = Date.now();
+            await fetchOAuthToken();
+        }
         throw Error('Invalid twitter oauth tokens');
+    } else if (!lastRequestSuccess) {
+        lastRequestSuccess = true;
+        if (Date.now() - lastFetchTime > 1000 * 60 * 60) {
+            lastFetchTime = Date.now();
+            await fetchOAuthToken();
+        }
     }
 
+    lastRequestSuccess = false;
     const oauth = OAuth({
         consumer: {
             key: consumerKey,
@@ -50,6 +71,7 @@ const twitterGot = async (url, params) => {
         headers: oauth.toHeader(oauth.authorize(requestData, token)),
     });
 
+    lastRequestSuccess = true;
     return response.data;
 };
 
```

and then set the following environment variables in your docker compose file.

```yaml
- TWITTER_OAUTH_TOKEN_FETCH_URL=<url to xtoken>
```

## License

MIT
