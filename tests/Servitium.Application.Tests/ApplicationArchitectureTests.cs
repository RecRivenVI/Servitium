using System;
using System.Linq;
using System.Reflection;

using Servitium.Application.Authorization;
using Servitium.Application.Instances;

using Xunit;

namespace Servitium.Application.Tests;

public sealed class ApplicationArchitectureTests
{
    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-022")]
    public void 全部用例_注入单一授权服务或显式标记无需授权()
    {
        Type[] useCases = [.. typeof(RegisterExternalDirectoryUseCase).Assembly
            .GetExportedTypes()
            .Where(type => type.IsClass && !type.IsAbstract && type.Name.EndsWith("UseCase", StringComparison.Ordinal))];

        Assert.NotEmpty(useCases);
        foreach (Type useCase in useCases)
        {
            bool exempt = useCase.GetCustomAttribute<AuthorizationNotRequiredAttribute>() is not null;
            bool injectsAuthorization = useCase
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IAuthorizationService));
            Assert.True(exempt || injectsAuthorization, $"{useCase.FullName} does not use the central authorization service.");
        }
    }
}
