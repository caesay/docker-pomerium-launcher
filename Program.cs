using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dockerSock = Environment.GetEnvironmentVariable("DL_DOCKER");
var port = Environment.GetEnvironmentVariable("DL_PORT");
var pomConfig = Environment.GetEnvironmentVariable("DL_POMERIUM");
var hideContainers = Environment.GetEnvironmentVariable("DL_HIDE");
var customContainers = Environment.GetEnvironmentVariable("DL_EXTRA");
var pageTitle = Environment.GetEnvironmentVariable("DL_TITLE");
var editConfigUrl = Environment.GetEnvironmentVariable("DL_EDIT_CONFIG_URL");
var editContainerUrl = Environment.GetEnvironmentVariable("DL_CONFIGURE_CONTAINER_URL");

var deserializer = new DeserializerBuilder()
    .IgnoreUnmatchedProperties()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

DockerClient dockerClient = null;
DockerClient GetDockerClient()
{
    dockerClient ??= new DockerClientConfiguration(new Uri(dockerSock)).CreateClient();
    return dockerClient;
}

if (!String.IsNullOrEmpty(port))
{
    app.Urls.Add("http://*:" + port);
}

async Task StaticPage(HttpContext ctx, string jspath, object jsonData = null)
{
    string json = "null";

    if (jsonData != null)
    {
        json = JsonSerializer.Serialize(jsonData);
    }

    var resp = $$"""
    <html>
    <head>
        <link rel="stylesheet" type="text/css" href="/index.css">
        <script src="https://cdnjs.cloudflare.com/ajax/libs/lodash.js/4.17.21/lodash.min.js" integrity="sha512-WFN04846sdKMIP5LKNphMaWzU7YpMyCU245etK3g/2ARYbPK9Ub18eG+ljU96qKRCWh+quCY7yefSmlkQw1ANQ==" crossorigin="anonymous" referrerpolicy="no-referrer"></script>
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
        <link href="https://fonts.googleapis.com/css2?family=Titillium+Web:ital,wght@0,300;0,400;0,600;1,400&display=swap" rel="stylesheet">
        <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.2/css/all.min.css" integrity="sha512-z3gLpd7yknf1YoNbCzqRKc4qyor8gaKU1qmn+CShxbuBusANI9QpRohGBreCFkKxLhei6S9CQXFEbbKuqLg0DA==" crossorigin="anonymous" referrerpolicy="no-referrer" />
        <link rel="icon" type="image/png" sizes="32x32" href="/launchbox-32.png">
        <link rel="icon" type="image/png" sizes="16x16" href="/launchbox-16.png">
        {{(pageTitle == null ? "" : $"<title>{pageTitle}</title>")}}
        <meta name="viewport" content="width=device-width, initial-scale=1">
    </head>
    <body>
        <script> window.myData = {{json}}; </script>
        <script type="module" src="/{{jspath}}"></script>
    </body>
    </html>
    """;

    ctx.Response.ContentType = "text/html";
    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(resp));
}

app.MapGet("/", async (ctx) =>
{
    await StaticPage(ctx, "index.js");
});

app.MapGet("/{fileName:regex(^[\\d\\w-]+(\\.js|\\.css|\\.png)$)}", async (string fileName) =>
{
    try
    {
        byte[] bytes = null;
#if DEBUG
        if (Debugger.IsAttached)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", fileName);
            bytes = File.ReadAllBytes(path);
        }
        else
        {
#endif
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "DockerLauncher." + fileName;
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var memoryStream = new MemoryStream())
            {
                stream.CopyTo(memoryStream);
                bytes = memoryStream.ToArray();
            }
#if DEBUG
        }
#endif

        if (fileName.EndsWith(".js"))
        {
            return Results.File(bytes, "text/javascript");
        }
        else if (fileName.EndsWith(".css"))
        {
            return Results.File(bytes, "text/css");
        }
        else if (fileName.EndsWith(".png"))
        {
            return Results.File(bytes, "image/png", fileName);
        }
    }
    catch (Exception)
    {
        // log?   
    }

    return Results.NotFound();

});

app.MapGet("/status", async () =>
{
    return Results.Json(await GetAllContainers());
});

app.MapGet("/status/{containerName}", async (string containerName) =>
{
    var container = await GetContainer(containerName, false);
    if (container == null) return Results.NotFound();
    return Results.Json(container);
});

app.MapGet("/start/{containerName}", async (string containerName) =>
{
    var container = await GetContainer(containerName, false);
    if (container == null) return Results.NotFound();
    await GetDockerClient().Containers.StartContainerAsync(container.Id, new());
    return Results.Redirect("/");
});

app.MapGet("/stop/{containerName}", async (string containerName) =>
{
    var container = await GetContainer(containerName, false);
    if (container == null) return Results.NotFound();
    await GetDockerClient().Containers.StopContainerAsync(container.Id, new());
    return Results.Redirect("/");
});

app.MapGet("/restart/{containerName}", async (string containerName) =>
{
    var container = await GetContainer(containerName, false);
    if (container == null) return Results.NotFound();
    await GetDockerClient().Containers.RestartContainerAsync(container.Id, new());
    return Results.Redirect("/");
});

app.MapGet("/launch/{did}", async (ctx) =>
{
    var dockerName = ctx.Request.RouteValues["did"] as string;
    var container = await GetContainer(dockerName, false);
    if (container == null)
    {
        ctx.Response.StatusCode = 404;
    }
    else if (String.IsNullOrWhiteSpace(container.NavigateUrl))
    {
        ctx.Response.StatusCode = 412;
    }
    else if (container.State == "running")
    {
        ctx.Response.Redirect(container.NavigateUrl);
    }
    else if (container.State == "exited")
    {
        await GetDockerClient().Containers.StartContainerAsync(container.Id, new());
        await StaticPage(ctx, "launch.js", new { containerName = container.Name, navigateUrl = container.NavigateUrl });
    }
});

app.Run();

ContainerItem[] MapContainerResponse(IList<ContainerListResponse> ca, bool launchRoutes = true)
{
    var p = deserializer.Deserialize<PomeriumRoot>(File.ReadAllText(pomConfig));
    var routes = p.Policy.ToDictionary(z => new Uri(z.To).Host, z => z.From, StringComparer.OrdinalIgnoreCase);

    var hidden = String.IsNullOrWhiteSpace(hideContainers)
        ? new string[0]
        : hideContainers.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var query = from c in ca
                let networks = c.NetworkSettings?.Networks?.ToArray() ?? new KeyValuePair<string, EndpointSettings>[0]
                where networks.All(z => !hidden.Contains(z.Key))
                let n = networks.FirstOrDefault()
                let name = c.Names.First().TrimStart('/')
                where !hidden.Contains(name)
                select new ContainerItem
                {
                    Name = name,
                    State = c.State,
                    Id = c.ID,
                    IconUrl = c.Labels.Where(l => l.Key.Equals("net.unraid.docker.icon")).Select(l => l.Value).FirstOrDefault(),
                    NetworkName = n.Key,
                    IpAddress = n.Value?.IPAddress,
                    NavigateUrl = routes.ContainsKey(name) ? (launchRoutes ? $"/launch/{name}" : routes[name]) : null,
                    Running = c.State == "running",
                    Mounts = c.Mounts?.Select(z => z.Source)?.ToArray() ?? new string[0],
                    Ports = c.Ports.DistinctBy(p => p.PrivatePort).DistinctBy(p => p.PublicPort).OrderBy(p => p.PrivatePort).ToArray(),
                    ExtraActions = new(),
                };

    var computed = query.ToArray();

    if (editConfigUrl != null)
    {
        if (!editConfigUrl.Contains(":") || !editConfigUrl.Contains("{path}"))
            throw new Exception("Invalid format for DL_EDIT_URL_TEMPLATE");
        var spl = editConfigUrl.Split(':');
        if (spl.Length < 2)
            throw new Exception("Invalid format for DL_EDIT_URL_TEMPLATE");
        var pathPrefix = spl[0];
        var substitution = String.Join(":", spl.Skip(1));

        foreach (var c in computed)
        {
            var m = (c.Mounts?.FirstOrDefault(m => m.StartsWith("/mnt/user/appdata/" + c.Name))
                    ?? c.Mounts.FirstOrDefault(m => m.StartsWith("/mnt/user/appdata/") && m.Contains(c.Name)));

            if (m != null && m.StartsWith(pathPrefix))
            {
                m = m.Substring(pathPrefix.Length);

                var mParts = m.Split("/");
                var mAppIndex = Array.IndexOf(mParts, "appdata");

                m = String.Join("/", mParts.Take(mAppIndex + 2));


                var url = substitution.Replace("{path}", m);
                c.ExtraActions["\uf013 Edit AppData"] = url;
            }
        }
    }

    if (editContainerUrl != null)
    {
        if (!editContainerUrl.Contains("{name}"))
            throw new Exception("Invalid format for DL_CONFIGURE_CONTAINER_URL");

        foreach (var c in computed)
        {
            c.ExtraActions["\uf1b2 Docker Template"] = editContainerUrl.Replace("{name}", c.Name);
        }
    }

    ContainerItem[] customs = new ContainerItem[0];

    if (!String.IsNullOrWhiteSpace(customContainers))
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,

        };
        customs = JsonSerializer.Deserialize<ContainerItem[]>(customContainers.Trim(), options);
    }

    if (customs.Any())
    {
        var dict = computed.ToDictionary(q => q.Name, q => q);
        foreach (var c in customs)
        {
            if (dict.ContainsKey(c.Name))
            {
                var dockerObj = dict[c.Name];
                var properties = typeof(ContainerItem).GetProperties();
                foreach (var prop in properties)
                {
                    var cVal = prop.GetValue(c);
                    if (cVal != null)
                    {
                        prop.SetValue(dockerObj, cVal);
                    }
                }
            }
            else
            {
                dict.Add(c.Name, c);
            }
        }

        return dict.Values.OrderBy(c => c.Name).ToArray();
    }

    return computed.OrderBy(c => c.Name).ToArray();
}

async Task<ContainerItem> GetContainer(string name, bool launchRoutes = true)
{
    var req = new ContainersListParameters
    {
        All = true,
        Filters = new Dictionary<string, IDictionary<string, bool>>
        {
            {"name", new Dictionary<string, bool>
                {
                    {name, true}
                }
            }
        }
    };

    var ca = await GetDockerClient().Containers.ListContainersAsync(req);
    if (!ca.Any())
    {
        return null;
    }

    return MapContainerResponse(ca, launchRoutes).First(c => c.Name == name);
}

async Task<ContainerItem[]> GetAllContainers(bool launchRoutes = true)
{
    var ca = await GetDockerClient().Containers.ListContainersAsync(new ContainersListParameters { All = true, Limit = 1000 });
    if (!ca.Any())
    {
        return new ContainerItem[0];
    }
    return MapContainerResponse(ca, launchRoutes);
}

record ContainerItem
{
    public string Name { get; set; }
    public string State { get; set; }
    public bool? Running { get; set; }
    public string Id { get; set; }
    public string IconUrl { get; set; }
    public string NavigateUrl { get; set; }
    public string IpAddress { get; set; }
    public string NetworkName { get; set; }
    public string[] Mounts { get; set; }
    public Port[] Ports { get; set; }
    public Dictionary<string, string> ExtraActions { get; set; }
}

class PomeriumRoute
{
    public string From { get; set; }
    public string To { get; set; }
}

class PomeriumRoot
{
    public PomeriumRoute[] Policy { get; set; }
}