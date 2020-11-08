//https://mderriey.com/2020/09/12/resolve-ef-core-interceptors-with-dependency-injection/

using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MovieCatalog.Azure.TokenUtils
{
    public interface IAzureSqlTokenProvider
    {
        Task<(string AccessToken, DateTimeOffset ExpiresOn)> GetAccessTokenAsync(CancellationToken cancellationToken = default);
        (string AccessToken, DateTimeOffset ExpiresOn) GetAccessToken();
    }

    public class AzureIdentityAzureSqlTokenProvider : IAzureSqlTokenProvider
    {
        private static readonly string[] _azureSqlScopes = new[]
        {
        "https://database.windows.net//.default"
        };

        public async Task<(string AccessToken, DateTimeOffset ExpiresOn)> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var tokenRequestContext = new TokenRequestContext(_azureSqlScopes);
            var token = await new DefaultAzureCredential().GetTokenAsync(tokenRequestContext, cancellationToken);

            return (token.Token, token.ExpiresOn);
        }

        public (string AccessToken, DateTimeOffset ExpiresOn) GetAccessToken()
        {
            var tokenRequestContext = new TokenRequestContext(_azureSqlScopes);
            var token = new DefaultAzureCredential().GetToken(tokenRequestContext, default);

            return (token.Token, token.ExpiresOn);
        }
    }

    // Decorator that caches tokens in the in-memory cache
    public class CacheAzureSqlTokenProvider : IAzureSqlTokenProvider
    {
        private const string _cacheKey = nameof(CacheAzureSqlTokenProvider);
        private readonly IAzureSqlTokenProvider _inner;
        private readonly IMemoryCache _cache;

        public CacheAzureSqlTokenProvider(
            IAzureSqlTokenProvider inner,
            IMemoryCache cache)
        {
            _inner = inner;
            _cache = cache;
        }

        public async Task<(string AccessToken, DateTimeOffset ExpiresOn)> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return await _cache.GetOrCreateAsync(_cacheKey, async cacheEntry =>
            {
                var (token, expiresOn) = await _inner.GetAccessTokenAsync(cancellationToken);

                // AAD access tokens have a default lifetime of 1 hour, so we take a small safety margin
                cacheEntry.SetAbsoluteExpiration(expiresOn.AddMinutes(-10));

                return (token, expiresOn);
            });
        }

        public (string AccessToken, DateTimeOffset ExpiresOn) GetAccessToken()
        {
            return _cache.GetOrCreate(_cacheKey, cacheEntry =>
            {
                var (token, expiresOn) = _inner.GetAccessToken();

                // AAD access tokens have a default lifetime of 1 hour, so we take a small safety margin
                cacheEntry.SetAbsoluteExpiration(expiresOn.AddMinutes(-10));

                return (token, expiresOn);
            });
        }
    }

    // The interceptor is now using the token provider abstraction
    public class AadAuthenticationDbConnectionInterceptor : DbConnectionInterceptor
    {
        private readonly IAzureSqlTokenProvider _tokenProvider;

        public AadAuthenticationDbConnectionInterceptor(IAzureSqlTokenProvider tokenProvider)
        {
            _tokenProvider = tokenProvider;
        }

        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            var sqlConnection = (SqlConnection)connection;
            if (ConnectionNeedsAccessToken(sqlConnection))
            {
                var (token, _) = _tokenProvider.GetAccessToken();
                sqlConnection.AccessToken = token;
            }

            return base.ConnectionOpening(connection, eventData, result);
        }

        public override async Task<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            var sqlConnection = (SqlConnection)connection;
            if (ConnectionNeedsAccessToken(sqlConnection))
            {
                var (token, _) = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
                sqlConnection.AccessToken = token;
            }

            return await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
        }

        private static bool ConnectionNeedsAccessToken(SqlConnection connection)
        {
            //
            // Only try to get a token from AAD if
            //  - We connect to an Azure SQL instance; and
            //  - The connection doesn't specify a username.
            //
            var connectionStringBuilder = new SqlConnectionStringBuilder(connection.ConnectionString);

            return connectionStringBuilder.DataSource.Contains("database.windows.net", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(connectionStringBuilder.UserID);
        }
    }
}
