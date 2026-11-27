using System.Collections;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using Xunit.Abstractions;

namespace StreetBiz.API.Tests;

/// <summary>
/// Every controller action of the API, found from the app's own route table, is called once with
/// sample route values and a sample JSON body built from its parameter types. None may be
/// unroutable (404/405) or refuse the body format (415), and the actions must reach MediatR: a
/// renamed route, a lost [Http*] verb or a broken binding shows up here for the whole API at once.
/// </summary>
public sealed class ControllerRouteSweepTests : IDisposable
{
    private const string Key = "route-sweep-tests-signing-key-0123456789abcdef";
    private const long UserId = 5;
    private const long SessionId = 40;
    private readonly ITestOutputHelper output;
    private readonly RecordingSender sender = new();
    private readonly ExecutedActions executed = new();
    private readonly WebApplicationFactory<Program> host;

    public ControllerRouteSweepTests(ITestOutputHelper output)
    {
        this.output = output;
        var now = DateTime.UtcNow;
        var sessions = new Mock<ISessionRepository>();
        sessions.Setup(s => s.GetByIdAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSession(SessionId, UserId, "test", "127.0.0.1", now.AddHours(-1), now, now.AddDays(1), null));
        var users = new Mock<IUserAccountRepository>();
        users.Setup(u => u.GetByIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppUser(UserId, "0905000101", "x", "Test", RoleCodes.Vendor, 10, AccountStatuses.Active, now));
        host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:StreetBizDatabase"] = "Server=unused;Database=unused;Integrated Security=True",
                ["Jwt:SigningKey"] = Key,
                ["Jwt:Issuer"] = "StreetBiz",
                ["Jwt:Audience"] = "StreetBiz",
            };
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.Configure<MvcOptions>(o => o.Filters.Add(executed));
                services.RemoveAll<ISender>();
                services.AddSingleton<ISender>(sender);
                services.RemoveAll<ISessionRepository>();
                services.AddSingleton(sessions.Object);
                services.RemoveAll<IUserAccountRepository>();
                services.AddSingleton(users.Object);
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                             && d.ImplementationType?.Assembly.GetName().Name?.StartsWith("StreetBiz", StringComparison.Ordinal) == true).ToList())
                {
                    services.Remove(hosted);
                }
            });
        });
    }

    public void Dispose() => host.Dispose();

    private static string Token() =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("StreetBiz", "StreetBiz",
            [new Claim("sub", UserId.ToString()), new Claim("sid", SessionId.ToString()), new Claim("role", RoleCodes.Vendor)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)));

    private sealed record Call(string Method, string Url, string? Body, string Action);

    /// <summary>One call per (verb, controller action), with its route values and body filled in.</summary>
    private List<Call> Calls()
    {
        var calls = new List<Call>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (action is null) continue;
            var verbs = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            var path = "/" + string.Join("/", endpoint.RoutePattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
            {
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart literal => literal.Content,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart p => Uri.EscapeDataString(RouteValue(p, action)),
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternSeparatorPart sep => sep.Content,
                _ => "",
            }))));
            var query = string.Join("&", action.Parameters
                .Where(p => p.BindingInfo?.BindingSource == BindingSource.Query
                            || (p.BindingInfo?.BindingSource is null && IsSimple(p.ParameterType) && !endpoint.RoutePattern.Parameters.Any(rp => rp.Name == p.Name)))
                .Where(p => IsSimple(p.ParameterType))
                .Select(p => $"{p.Name}={Uri.EscapeDataString(SampleText(p.ParameterType, p.Name))}"));
            var bodyParam = action.Parameters.FirstOrDefault(p => p.BindingInfo?.BindingSource == BindingSource.Body
                || (p.BindingInfo?.BindingSource is null && !IsSimple(p.ParameterType) && p.ParameterType != typeof(CancellationToken)
                    && !typeof(IFormFile).IsAssignableFrom(p.ParameterType) && !typeof(IFormFileCollection).IsAssignableFrom(p.ParameterType)));
            foreach (var verb in verbs)
            {
                var body = verb is "GET" or "DELETE" || bodyParam is null ? null : JsonSerializer.Serialize(Sample(bodyParam.ParameterType, 0));
                calls.Add(new(verb, query.Length > 0 ? $"{path}?{query}" : path, body, $"{action.ControllerName}.{action.ActionName}"));
            }
        }
        return calls;
    }

    private static string RouteValue(Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart part, ControllerActionDescriptor action)
    {
        var type = action.Parameters.FirstOrDefault(p => string.Equals(p.Name, part.Name, StringComparison.OrdinalIgnoreCase))?.ParameterType;
        return type is null ? "1" : SampleText(type, part.Name);
    }

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
               || t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(DateTimeOffset);
    }

    private static string SampleText(Type t, string? name)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return name?.Contains("token", StringComparison.OrdinalIgnoreCase) == true ? "tok" : "a";
        if (t == typeof(bool)) return "true";
        if (t == typeof(Guid)) return Guid.Empty.ToString();
        if (t == typeof(DateOnly)) return "2026-11-01";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "2026-11-01T00:00:00Z";
        if (t == typeof(TimeOnly)) return "08:00:00";
        if (t.IsEnum) return Enum.GetNames(t)[0];
        return "1";
    }

    /// <summary>A sample value for a JSON body: every property filled, collections with one item.</summary>
    private static object? Sample(Type t, int depth)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return "a";
        if (u == typeof(bool)) return true;
        if (u == typeof(Guid)) return Guid.Empty;
        if (u == typeof(DateOnly)) return "2026-11-01";
        if (u == typeof(DateTime) || u == typeof(DateTimeOffset)) return "2026-11-01T00:00:00Z";
        if (u == typeof(TimeOnly)) return "08:00:00";
        if (u.IsEnum) return Enum.GetValues(u).GetValue(0);
        if (u.IsPrimitive || u == typeof(decimal)) return 1;
        if (depth > 3) return null;
        if (u.IsArray || (u != typeof(string) && typeof(IEnumerable).IsAssignableFrom(u)))
        {
            var element = u.IsArray ? u.GetElementType()! : u.GetGenericArguments().LastOrDefault() ?? typeof(object);
            if (u.IsGenericType && u.GetGenericArguments().Length == 2)
                return new Dictionary<string, object?> { ["a"] = Sample(u.GetGenericArguments()[1], depth + 1) };
            return new[] { Sample(element, depth + 1) };
        }
        if (u == typeof(object) || u.IsAbstract || u.IsInterface) return new Dictionary<string, object?>();
        var bag = new Dictionary<string, object?>();
        foreach (var p in u.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite || u.GetConstructors().Any(c => c.GetParameters().Length > 0)))
        {
            if (typeof(IFormFile).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(Stream)) continue;
            bag[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = Sample(p.PropertyType, depth + 1);
        }
        return bag;
    }

    [Fact]
    public async Task Every_action_is_routable_takes_its_body_and_reaches_mediatr()
    {
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token());
        var calls = Calls();
        Assert.True(calls.Count > 250, $"only {calls.Count} actions found");

        var broken = new List<string>();
        var dispatched = 0;
        foreach (var call in calls)
        {
            var before = sender.Requests.Count;
            executed.Last = null;
            using var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Url);
            if (call.Body is not null) request.Content = new StringContent(call.Body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            // An action may answer 404 itself (a missing file, a sandbox route switched off); only a
            // call that never reached its action is a routing or binding fault.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnsupportedMediaType
                && executed.Last is null)
            {
                broken.Add($"{(int)response.StatusCode} {call.Method} {call.Url} ({call.Action})");
            }
            if (sender.Requests.Count > before) dispatched++;
        }

        output.WriteLine($"{calls.Count} actions, {dispatched} reached MediatR");
        Assert.True(broken.Count == 0, "unroutable actions:\n" + string.Join("\n", broken));
        // Upload, health and redirect actions answer without a command; everything else must reach one.
        Assert.True(dispatched >= calls.Count * 0.85, $"only {dispatched} of {calls.Count} actions reached MediatR");
    }

    [Fact]
    public async Task Without_a_token_no_protected_action_sends_anything()
    {
        using var anonymous = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var protectedCalls = Calls().Where(c => c.Url.StartsWith("/api/vendor") || c.Url.StartsWith("/api/ward") || c.Url.StartsWith("/api/platform")).ToList();
        Assert.NotEmpty(protectedCalls);

        foreach (var call in protectedCalls)
        {
            using var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Url);
            if (call.Body is not null) request.Content = new StringContent(call.Body, Encoding.UTF8, "application/json");
            using var response = await anonymous.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{call.Method} {call.Url} answered {(int)response.StatusCode} without a token");
        }
        Assert.Empty(sender.Requests);
    }

    /// <summary>Records the action MVC actually ran for the last request.</summary>
    private sealed class ExecutedActions : IActionFilter
    {
        public string? Last { get; set; }
        public void OnActionExecuting(ActionExecutingContext context) => Last = context.ActionDescriptor.DisplayName;
        public void OnActionExecuted(ActionExecutedContext context) { }
    }

    private sealed class RecordingSender : ISender
    {
        public List<object> Requests { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(default(TResponse)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            lock (Requests) Requests.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => Empty<TResponse>();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => Empty<object?>();

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
