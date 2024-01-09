package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"strings"
	"time"

	"golang.org/x/net/proxy"
)

const (
	GUEST_TOKEN_URL = "https://api.twitter.com/1.1/guest/activate.json"
	FLOW_TOKEN_URL  = "https://api.twitter.com/1.1/onboarding/task.json?flow_name=welcome"
	OAUTH_TOKEN_URL = "https://api.twitter.com/1.1/onboarding/task.json"
	CONTENT_TYPE    = "application/json"
	USER_AGENT      = "TwitterAndroid/10.10.0"
)

var AUTHORIZATION string = os.Getenv("AUTHORIZATION")
var PROXY_LIST_URL string = os.Getenv("PROXY_LIST_URL")

type JsonObject = map[string]interface{}
type JsonArray = []interface{}

func getProxyList() []string {
	resp, err := http.Get(PROXY_LIST_URL)
	if err != nil {
		panic(err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		panic(err)
	}
	return strings.Split(string(body), "\n")
}

func getGuestToken(ctx context.Context, client *http.Client) (string, error) {
	req, err := http.NewRequestWithContext(ctx, "POST", GUEST_TOKEN_URL, nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("Authorization", AUTHORIZATION)

	resp, err := client.Do(req)
	if resp != nil {
		defer resp.Body.Close()
	}
	select {
	case <-ctx.Done():
		return "", ctx.Err()
	default:
		if err != nil {
			return "", err
		}
	}

	var obj JsonObject
	err = json.NewDecoder(resp.Body).Decode(&obj)
	if err != nil {
		return "", err
	}

	guestToken, ok := obj["guest_token"].(string)
	if !ok {
		return "", fmt.Errorf("getGuestToken: expected string, got %T", obj["guest_token"])
	}
	return guestToken, nil
}

func getFlowToken(ctx context.Context, client *http.Client, guestToken string) (string, error) {
	const DATA = `{"flow_token":null,"input_flow_data":{"flow_context":{"start_location":{"location":"splash_screen"}}}}`
	req, err := http.NewRequestWithContext(ctx, "POST", FLOW_TOKEN_URL, strings.NewReader(DATA))
	if err != nil {
		return "", err
	}

	req.Header.Set("Authorization", AUTHORIZATION)
	req.Header.Set("Content-Type", CONTENT_TYPE)
	req.Header.Set("User-Agent", USER_AGENT)
	req.Header.Set("X-Guest-Token", guestToken)

	resp, err := client.Do(req)
	if resp != nil {
		defer resp.Body.Close()
	}
	select {
	case <-ctx.Done():
		return "", ctx.Err()
	default:
		if err != nil {
			return "", err
		}
	}

	var obj JsonObject
	err = json.NewDecoder(resp.Body).Decode(&obj)
	if err != nil {
		return "", err
	}

	flowToken, ok := obj["flow_token"].(string)
	if !ok {
		return "", fmt.Errorf("getFlowToken: expected string, got %T", obj["flow_token"])
	}
	return flowToken, nil
}

func getOAuthToken(ctx context.Context, client *http.Client, guestToken, flowToken string) (string, string, error) {
	const DATA = `{"flow_token":"%s","subtask_inputs":[{"open_link":{"link":"next_link"},"subtask_id":"NextTaskOpenLink"}]}`
	req, err := http.NewRequestWithContext(ctx, "POST", OAUTH_TOKEN_URL, strings.NewReader(fmt.Sprintf(DATA, flowToken)))
	if err != nil {
		return "", "", err
	}

	req.Header.Set("Authorization", AUTHORIZATION)
	req.Header.Set("Content-Type", CONTENT_TYPE)
	req.Header.Set("User-Agent", USER_AGENT)
	req.Header.Set("X-Guest-Token", guestToken)

	resp, err := client.Do(req)
	if resp != nil {
		defer resp.Body.Close()
	}
	select {
	case <-ctx.Done():
		return "", "", ctx.Err()
	default:
		if err != nil {
			return "", "", err
		}
	}

	var obj JsonObject
	err = json.NewDecoder(resp.Body).Decode(&obj)
	if err != nil {
		return "", "", err
	}

	subTasks, ok := obj["subtasks"].(JsonArray)
	if !ok {
		return "", "", fmt.Errorf("getOAuthToken: expected JsonArray, got %T", obj["subtasks"])
	}
	subTask, ok := subTasks[0].(JsonObject)
	if !ok {
		return "", "", fmt.Errorf("getOAuthToken: expected JsonObject, got %T", subTasks[0])
	}
	openAccount, ok := subTask["open_account"].(JsonObject)
	if !ok {
		return "", "", fmt.Errorf("getOAuthToken: expected JsonObject, got %T", subTask["open_account"])
	}
	oauthToken, ok := openAccount["oauth_token"].(string)
	if !ok {
		return "", "", fmt.Errorf("getOAuthToken: expected string, got %T", openAccount["oauth_token"])
	}
	oauthTokenSecret, ok := openAccount["oauth_token_secret"].(string)
	if !ok {
		return "", "", fmt.Errorf("getOAuthToken: expected string, got %T", openAccount["oauth_token_secret"])
	}
	return oauthToken, oauthTokenSecret, nil
}

func formatRespText(oauthToken, oauthTokenSecret string) string {
	const FORMAT = "%s,%s"
	return fmt.Sprintf(FORMAT, oauthToken, oauthTokenSecret)
}

func getClient(proxyUrl string) *http.Client {
	dialer, err := proxy.SOCKS5("tcp", proxyUrl, nil, proxy.Direct)
	if err != nil {
		panic(err)
	}
	dialContext := func(ctx context.Context, network, addr string) (net.Conn, error) {
		return dialer.Dial(network, addr)
	}
	transport := &http.Transport{DialContext: dialContext}
	return &http.Client{Transport: transport}
}

func getRespText() string {
	proxyList := getProxyList()
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	resChan := make(chan string)
	defer close(resChan)
	for _, proxy := range proxyList {
		// don't capture loop variable
		proxy := proxy
		go func() {
			fmt.Println("Trying", proxy)

			client := getClient(proxy)

			guestToken, err := getGuestToken(ctx, client)
			if err != nil {
				fmt.Println(err)
				return
			}
			fmt.Println("guestToken:", guestToken)

			flowToken, err := getFlowToken(ctx, client, guestToken)
			if err != nil {
				fmt.Println(err)
				return
			}
			fmt.Println("flowToken:", flowToken)

			oauthToken, oauthTokenSecret, err := getOAuthToken(ctx, client, guestToken, flowToken)
			if err != nil {
				fmt.Println(err)
				return
			}
			fmt.Println("oauthToken:", oauthToken)
			resChan <- formatRespText(oauthToken, oauthTokenSecret)
		}()
	}

	return <-resChan
}

func handler(w http.ResponseWriter, r *http.Request) {
	// verify token
	token := r.URL.Query().Get("token")
	if token != os.Getenv("TOKEN") {
		w.WriteHeader(http.StatusForbidden)
		return
	}

	w.Write([]byte(getRespText()))
}

func main() {
	http.HandleFunc("/", handler)
	http.ListenAndServe(":80", nil)
}
