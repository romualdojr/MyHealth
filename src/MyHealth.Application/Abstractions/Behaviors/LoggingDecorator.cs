using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Application.Abstractions.Behaviors;

/// <summary>
/// Logs the request name and outcome only. Request payloads are never logged because they may carry health data.
/// </summary>
internal static class LoggingDecorator
{
    internal sealed class CommandHandler<TCommand>(ICommandHandler<TCommand> inner, ILogger<TCommand> logger)
        : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
        {
            var result = await inner.Handle(command, cancellationToken);
            Log(logger, typeof(TCommand).Name, result);
            return result;
        }
    }

    internal sealed class CommandHandler<TCommand, TResponse>(ICommandHandler<TCommand, TResponse> inner, ILogger<TCommand> logger)
        : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
        {
            var result = await inner.Handle(command, cancellationToken);
            Log(logger, typeof(TCommand).Name, result);
            return result;
        }
    }

    internal sealed class QueryHandler<TQuery, TResponse>(IQueryHandler<TQuery, TResponse> inner, ILogger<TQuery> logger)
        : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
        {
            var result = await inner.Handle(query, cancellationToken);
            Log(logger, typeof(TQuery).Name, result);
            return result;
        }
    }

    private static void Log(ILogger logger, string requestName, Result result)
    {
        if (result.IsSuccess)
        {
            logger.LogInformation("{Request} succeeded", requestName);
        }
        else
        {
            logger.LogWarning("{Request} failed with {ErrorCode}", requestName, result.Error.Code);
        }
    }
}
