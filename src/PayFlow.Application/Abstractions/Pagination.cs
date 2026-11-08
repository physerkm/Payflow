using Microsoft.EntityFrameworkCore;

namespace PayFlow.Application.Abstractions;

public sealed record PageRequest(int Page = 1, int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int NormalizedPage => Page < 1 ? 1 : Page;

    public int NormalizedPageSize => PageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => PageSize,
    };

    public int Skip => (NormalizedPage - 1) * NormalizedPageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Sort specification parsed from <c>field</c> or <c>field:desc</c>, restricted to an allowlist.</summary>
public sealed record SortSpec(string Field, bool Descending)
{
    public static SortSpec Parse(string? sort, IReadOnlyCollection<string> allowedFields, SortSpec fallback)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return fallback;
        }

        var parts = sort.Split(':', 2, StringSplitOptions.TrimEntries);
        var field = parts[0];
        var match = allowedFields.FirstOrDefault(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new RequestValidationException("sort", $"Sorting by '{field}' is not allowed. Allowed: {string.Join(", ", allowedFields)}.");
        }

        var descending = parts.Length == 2 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
        if (parts.Length == 2 && !descending && !parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase))
        {
            throw new RequestValidationException("sort", "Sort direction must be 'asc' or 'desc'.");
        }

        return new SortSpec(match, descending);
    }
}

public static class QueryableExtensions
{
    public static async Task<PagedResult<TResult>> ToPagedResultAsync<TSource, TResult>(
        this IQueryable<TSource> query,
        PageRequest page,
        Func<IQueryable<TSource>, IQueryable<TResult>> projection,
        CancellationToken cancellationToken)
    {
        var total = await query.LongCountAsync(cancellationToken);
        var items = await projection(query.Skip(page.Skip).Take(page.NormalizedPageSize)).ToListAsync(cancellationToken);
        return new PagedResult<TResult>(items, page.NormalizedPage, page.NormalizedPageSize, total);
    }

    public static IOrderedQueryable<T> OrderBy<T, TKey>(this IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, TKey>> key, bool descending) =>
        descending ? query.OrderByDescending(key) : Queryable.OrderBy(query, key);
}
