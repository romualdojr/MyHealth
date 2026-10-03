using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using MyHealth.Infrastructure.Identity;

namespace MyHealth.ApiService.Infrastructure;

/// <summary>Documents the optional tenant selector on every operation, so the API reference can act for a tenant.</summary>
internal sealed class TenantHeaderOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = TenantHeader.Name,
            In = ParameterLocation.Header,
            Required = false,
            Description = "The organization to act for (required for professional and tenant-administration endpoints). "
                          + "Honoured only when you are an active member of it.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
        });
        return Task.CompletedTask;
    }
}
