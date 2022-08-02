using System;
using Microsoft.AspNetCore.SignalR.Client;

namespace EtheirysSynchronos.WebAPI.Utils;

public class ForeverRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        return TimeSpan.FromSeconds(new Random().Next(5, 20));
    }
}