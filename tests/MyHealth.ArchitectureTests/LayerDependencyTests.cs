using System.Reflection;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using NetArchTest.Rules;

namespace MyHealth.ArchitectureTests;

/// <summary>Clean Architecture rule: dependencies point inwards (API → Infrastructure → Application → Domain).</summary>
public sealed class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(Entity<>).Assembly;
    private static readonly Assembly Application = typeof(ICommand).Assembly;
    private static readonly Assembly Infrastructure = typeof(MyHealth.Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_else_in_the_solution_nor_on_frameworks()
    {
        var result = Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny("MyHealth.Application", "MyHealth.Infrastructure", "MyHealth.ApiService", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_web()
    {
        var result = Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny("MyHealth.Infrastructure", "MyHealth.ApiService", "Microsoft.AspNetCore", "Npgsql")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_api()
    {
        var result = Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOn("MyHealth.ApiService").GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Handlers_are_internal_and_sealed()
    {
        var result = Types.InAssembly(Application)
            .That().ImplementInterface(typeof(ICommandHandler<>))
            .Or().ImplementInterface(typeof(ICommandHandler<,>))
            .Or().ImplementInterface(typeof(IQueryHandler<,>))
            .Should().NotBePublic().And().BeSealed()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Domain_entities_do_not_expose_public_setters()
    {
        var offenders = Domain.GetTypes()
            .Where(t => t.IsClass && IsEntity(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        offenders.ShouldBeEmpty();
    }

    private static bool IsEntity(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Entity<>))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
