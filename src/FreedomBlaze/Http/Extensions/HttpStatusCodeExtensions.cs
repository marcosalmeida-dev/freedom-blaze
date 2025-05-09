using System.Net;

namespace FreedomBlaze.Http.Extensions;

public static class HttpStatusCodeExtensions
{
	public static string ToReasonString(this HttpStatusCode me)
	{
		using var message = new HttpResponseMessage(me);
		return message.ReasonPhrase ?? "Unknown reason";
	}
}
