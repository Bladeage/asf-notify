using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Web;
using ArchiSteamFarm.Web.Responses;

namespace ASFNotify.Notifiers;

internal static class NotifierHttp {
	// Posts JSON through ASF's shared WebBrowser (inherits its proxy/TLS/timeout). The body is written
	// with Utf8JsonWriter rather than JsonSerializer, which isn't available on ASF's trimmed runtime.
	internal static async Task<bool> PostJsonAsync(Uri endpoint, Action<Utf8JsonWriter> writeBody, IReadOnlyCollection<KeyValuePair<string, string>>? headers, CancellationToken cancellationToken) {
		WebBrowser? webBrowser = ASF.WebBrowser;

		if (webBrowser == null) {
			ASF.ArchiLogger.LogNullError(webBrowser);

			return false;
		}

		byte[] body;

		using (MemoryStream stream = new()) {
			using (Utf8JsonWriter writer = new(stream)) {
				writer.WriteStartObject();
				writeBody(writer);
				writer.WriteEndObject();
			}

			body = stream.ToArray();
		}

		using HttpContent content = new ByteArrayContent(body);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

		// maxTries: 1 because the content can only be sent once; the dispatcher handles retries.
		BasicResponse? response = await webBrowser.UrlPost(
			endpoint,
			headers,
			content,
			requestOptions: WebBrowser.ERequestOptions.ReturnClientErrors | WebBrowser.ERequestOptions.ReturnServerErrors,
			maxTries: 1,
			cancellationToken: cancellationToken
		).ConfigureAwait(false);

		if (response == null) {
			return false;
		}

		if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices) {
			return true;
		}

		ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] {endpoint.Host} responded with HTTP {(int) response.StatusCode} ({response.StatusCode}).");

		return false;
	}
}
