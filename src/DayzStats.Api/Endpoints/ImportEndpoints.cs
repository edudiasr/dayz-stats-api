using DayzStats.Api.Import;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace DayzStats.Api.Endpoints;

public sealed class ImportOptions
{
    /// <summary>Pasta "profiles" do servidor, de onde POST /api/import lê os .ADM.</summary>
    public string? Folder { get; set; }

    /// <summary>Se preenchida, as rotas de importação exigem o header X-Api-Key com esse valor.</summary>
    public string? ApiKey { get; set; }
}

public static class ImportEndpoints
{
    private const long MaxUploadBytes = 200 * 1024 * 1024;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var import = app.MapGroup("/api/import")
            .WithTags("Importação")
            .AddEndpointFilter(RequireApiKey);

        import.MapPost("/", ImportFolder)
            .WithSummary("Importa os .ADM novos da pasta configurada em Import:Folder");

        import.MapPost("/file", ImportFile)
            .WithSummary("Importa um .ADM enviado no corpo da requisição (multipart/form-data)")
            .DisableAntiforgery();

        return app;
    }

    private static async Task<Results<Ok<ImportResult>, ProblemHttpResult>> ImportFolder(
        LogImporter importer, IOptions<ImportOptions> options, CancellationToken ct)
    {
        var folder = options.Value.Folder;
        if (string.IsNullOrWhiteSpace(folder))
            return TypedResults.Problem("Configure Import:Folder com a pasta profiles do servidor.", statusCode: 400);
        if (!Directory.Exists(folder))
            return TypedResults.Problem($"Pasta não encontrada: {folder}", statusCode: 400);

        return TypedResults.Ok(await importer.ImportFolderAsync(folder, ct));
    }

    private static async Task<Results<Ok<ImportResult>, ProblemHttpResult>> ImportFile(
        IFormFile file, LogImporter importer, CancellationToken ct)
    {
        if (!Path.GetExtension(file.FileName).Equals(".adm", StringComparison.OrdinalIgnoreCase))
            return TypedResults.Problem("Envie um arquivo .ADM.", statusCode: 400);
        if (file.Length == 0 || file.Length > MaxUploadBytes)
            return TypedResults.Problem("Arquivo vazio ou maior que 200 MB.", statusCode: 400);

        await using var stream = file.OpenReadStream();
        return TypedResults.Ok(await importer.ImportAsync(Path.GetFileName(file.FileName), stream, ct));
    }

    private static async ValueTask<object?> RequireApiKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<ImportOptions>>().Value.ApiKey;
        if (!string.IsNullOrEmpty(expected))
        {
            var sent = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(sent), System.Text.Encoding.UTF8.GetBytes(expected)))
                return TypedResults.Unauthorized();
        }
        return await next(context);
    }
}
