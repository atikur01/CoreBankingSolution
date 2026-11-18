using System.Text.RegularExpressions;

namespace CoreBanking.Tests.Web;

public static partial class FormClient
{
    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    public static async Task<string> TokenAsync(this HttpClient client, string pageUrl)
    {
        var html = await client.GetStringAsync(pageUrl);
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, $"No antiforgery token found on {pageUrl}");
        return match.Groups[1].Value;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string tokenPage, string postUrl, IDictionary<string, string> fields)
    {
        var token = await client.TokenAsync(tokenPage);
        var data = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(data));
    }

    public static async Task<HttpResponseMessage> PostMultipartAsync(this HttpClient client, string tokenPage, string postUrl, IDictionary<string, string> fields)
    {
        var token = await client.TokenAsync(tokenPage);
        var content = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (key, value) in fields) content.Add(new StringContent(value), key);
        return await client.PostAsync(postUrl, content);
    }
}
