using System.Linq.Expressions;
using System.Reflection;

namespace Mercurius.TestInfrastructure;

// Seeds persisted state on domain entities whose setters are private, the way EF Core materializes them.
internal static class EntityStateExtensions
{
    public static T Set<T, TValue>(this T entity, Expression<Func<T, TValue>> property, TValue value)
        where T : class
    {
        var member = property.Body as MemberExpression
            ?? throw new ArgumentException("Expected a property access expression.", nameof(property));
        ((PropertyInfo)member.Member).SetValue(entity, value);
        return entity;
    }
}
