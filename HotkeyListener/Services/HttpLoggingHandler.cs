using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace HotkeyListener.Services;

internal sealed class HttpLoggingHandler : DelegatingHandler
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "X-Api-Key",
        "X-ApiKey",
        "Api-Key"
    };

    public HttpLoggingHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await LogRequestAsync(request, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await LogResponseAsync(request, response, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not TaskCanceledException)
        {
            ConsoleLog.Error($"HTTP request failed: {request.Method} {request.RequestUri} - {ex.Message}");
            response?.Dispose();
            throw;
        }
    }

    private static async Task LogRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"HTTP Request: {request.Method} {request.RequestUri}");
        AppendHeaders(builder, request.Headers, "Headers");

        if (request.Content is not null)
        {
            AppendHeaders(builder, request.Content.Headers, "Content Headers");
            var (bodyText, clone) = await BufferContentAsync(request.Content, cancellationToken).ConfigureAwait(false);
            request.Content = clone;

            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                builder.AppendLine("Body:");
                builder.AppendLine(bodyText);
            }
        }

        ConsoleLog.Info(builder.ToString().TrimEnd());
    }

    private static async Task LogResponseAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"HTTP Response: {(int)response.StatusCode} {response.ReasonPhrase} ({request.Method} {request.RequestUri})");
        AppendHeaders(builder, response.Headers, "Headers");

        if (response.Content is not null)
        {
            AppendHeaders(builder, response.Content.Headers, "Content Headers");
            var (bodyText, clone) = await BufferContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            response.Content = clone;

            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                builder.AppendLine("Body:");
                builder.AppendLine(bodyText);
            }
        }

        ConsoleLog.Info(builder.ToString().TrimEnd());
    }

    private static async Task<(string? BodyText, HttpContent Clone)> BufferContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var buffer = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var bodyText = DecodeBuffer(buffer, content.Headers.ContentType?.CharSet);

        var clone = CloneContent(content, buffer);
        content.Dispose();

        return (bodyText, clone);
    }

    private static HttpContent CloneContent(HttpContent source, byte[] buffer)
    {
        var clone = new ByteArrayContent(buffer);

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static string? DecodeBuffer(byte[] buffer, string? charset)
    {
        if (buffer.Length == 0)
        {
            return null;
        }

        Encoding encoding;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }
        else
        {
            encoding = Encoding.UTF8;
        }

        try
        {
            return encoding.GetString(buffer);
        }
        catch (DecoderFallbackException)
        {
            return $"[binary data: {buffer.Length} bytes]";
        }
    }

    private static void AppendHeaders(StringBuilder builder, HttpHeaders headers, string title)
    {
        var hasAny = false;
        foreach (var header in headers)
        {
            if (!hasAny)
            {
                builder.AppendLine($"{title}:");
                hasAny = true;
            }

            var value = string.Join(", ", header.Value);

            builder.AppendLine($"  {header.Key}: {value}");
        }

        if (!hasAny)
        {
            builder.AppendLine($"{title}: (none)");
        }
    }
}