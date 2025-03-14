using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using GraphQL.Resolvers;

namespace GraphQL.Types;

/// <summary>
/// Helper methods for auto-registering graph types, <see cref="Builders.FieldBuilder{TSourceType, TReturnType}.ResolveDelegate(Delegate?)">Resolve</see>,
/// schema builder method builders, and <see cref="NameFieldResolver"/>.
/// </summary>
public static class AutoRegisteringHelper
{
    /// <summary>
    /// Constructs a field resolver for the specified field, property or method with the specified instance expression.
    /// Does not build accompanying query arguments for detected method parameters.
    /// Does not allow overriding build behavior.
    /// <br/><br/>
    /// An example of an instance expression would be as follows:
    /// <code>context =&gt; (TSourceType)context.Source</code>
    /// </summary>
    public static IFieldResolver BuildFieldResolver(MemberInfo memberInfo, Type? sourceType, FieldType? fieldType, LambdaExpression instanceExpression)
    {
        // this entire method is a simplification of AutoRegisteringObjectGraphType.BuildFieldType
        // but it does not provide the ability to override any behavior, and it does not return or
        // build query arguments
        if (memberInfo is FieldInfo fieldInfo)
        {
            return new MemberResolver(fieldInfo, instanceExpression);
        }
        else if (memberInfo is PropertyInfo propertyInfo)
        {
            return new MemberResolver(propertyInfo, instanceExpression);
        }
        else if (memberInfo is MethodInfo methodInfo)
        {
            var arguments = BuildFieldResolver_BuildMethodArguments(methodInfo, sourceType, fieldType);
            return new MemberResolver(methodInfo, instanceExpression, arguments);
        }

        throw new ArgumentOutOfRangeException(nameof(memberInfo), "Member must be a field, property or method.");
    }

    /// <summary>
    /// Constructs an event stream resolver for the specified method with the specified instance expression.
    /// Does not build accompanying query arguments for detected method parameters.
    /// Does not allow overriding build behavior.
    /// <br/><br/>
    /// An example of an instance expression would be as follows:
    /// <code>context =&gt; (TSourceType)context.Source</code>
    /// </summary>
    public static ISourceStreamResolver BuildSourceStreamResolver(MethodInfo methodInfo, Type? sourceType, FieldType? fieldType, LambdaExpression instanceExpression)
    {
        var arguments = BuildFieldResolver_BuildMethodArguments(methodInfo, sourceType, fieldType);
        return new SourceStreamMethodResolver(methodInfo, instanceExpression, arguments);
    }

    private static IList<LambdaExpression> BuildFieldResolver_BuildMethodArguments(MethodInfo methodInfo, Type? sourceType, FieldType? fieldType)
    {
        List<LambdaExpression> expressions = new();
        foreach (var parameterInfo in methodInfo.GetParameters())
        {
            var typeInformation = new TypeInformation(parameterInfo);
            typeInformation.ApplyAttributes(); // typically this is unnecessary, since this is primarily used to control the graph type of generated query arguments
            var argumentInfo = new ArgumentInformation(parameterInfo, sourceType, fieldType, typeInformation);
            argumentInfo.ApplyAttributes(); // necessary to allow [FromSource], [FromServices] and similar attributes to work
            var (queryArgument, expression) = argumentInfo.ConstructQueryArgument();
            if (queryArgument != null)
            {
                // even though the query argument is not used, it is necessary to apply attributes to the generated argument in case the name is overridden,
                // as the generated query argument's name is used within the expression for the call to GetArgument
                var attributes = parameterInfo.GetGraphQLAttributes();
                foreach (var attr in attributes)
                {
                    attr.Modify(queryArgument);
                    attr.Modify(queryArgument, parameterInfo);
                }
            }
            expression ??= GetParameterExpression(
                parameterInfo.ParameterType,
                queryArgument ?? throw new InvalidOperationException("Invalid response from ConstructQueryArgument: queryArgument and expression cannot both be null"));
            expressions.Add(expression);
        }
        return expressions;
    }

    /// <summary>
    /// Builds the following instance expression:
    /// <code>context =&gt; context.Source as TSourceType ?? (context.RequestServices ?? serviceProvider).GetService(sourceType) ?? throw new InvalidOperationException(...)</code>
    /// </summary>
    internal static LambdaExpression BuildInstanceExpressionForSchemaBuilder(Type sourceType, IServiceProvider serviceProvider)
    {
        // exception cannot occur here, so don't worry catching TargetInvokeException
        return (LambdaExpression)_buildSourceExpressionForSchemaBuilderInternalMethodInfo
            .MakeGenericMethod(sourceType)
            .Invoke(null, new object[] { serviceProvider })!;
    }

    private static readonly MethodInfo _buildSourceExpressionForSchemaBuilderInternalMethodInfo = typeof(AutoRegisteringHelper).GetMethod(nameof(BuildSourceExpressionForSchemaBuilderInternal), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static Expression<Func<IResolveFieldContext, T>> BuildSourceExpressionForSchemaBuilderInternal<T>(IServiceProvider serviceProvider)
        => context => BuildSourceExpressionForSchemaBuilderInternal_GetSource<T>(context, serviceProvider);
    private static T BuildSourceExpressionForSchemaBuilderInternal_GetSource<T>(IResolveFieldContext context, IServiceProvider serviceProvider)
    {
        var source = context.Source;

        var target = typeof(T).IsInstanceOfType(source)
            ? (T)source!
            : (T?)(context.RequestServices ?? serviceProvider).GetService(typeof(T));

        if (target == null)
        {
            var parentType = context.ParentType != null ? $"{context.ParentType.Name}." : null;
            throw new InvalidOperationException($"Could not resolve an instance of {typeof(T).Name} to execute {parentType}{context.FieldAst.Name}");
        }

        return target;
    }

    /// <summary>
    /// Scans a specific CLR type for <see cref="GraphQLAttribute"/> attributes and applies
    /// them to the specified <see cref="IGraphType"/>.
    /// Also scans the CLR type's owning module and assembly for globally-applied attributes.
    /// </summary>
    internal static void ApplyGraphQLAttributes<TSourceType>(IGraphType graphType)
    {
        // Description and deprecation reason are already set in ComplexGraphType<TSourceType> constructor

        // Apply derivatives of GraphQLAttribute
        var attributes = typeof(TSourceType).GetGraphQLAttributes();
        foreach (var attr in attributes)
        {
            attr.Modify(graphType);
            attr.Modify(graphType, typeof(TSourceType));
        }
    }

    /// <summary>
    /// Filters an enumeration of <see cref="PropertyInfo"/> values to exclude specified properties.
    /// </summary>
    internal static IEnumerable<PropertyInfo> ExcludeProperties<TSourceType>(IEnumerable<PropertyInfo> properties, params Expression<Func<TSourceType, object?>>[]? excludedProperties)
        => excludedProperties == null || excludedProperties.Length == 0
            ? properties
            : properties.Where(propertyInfo => !excludedProperties!.Any(p => GetPropertyName(p) == propertyInfo.Name));

    /// <summary>
    /// Creates a <see cref="FieldType"/> for the specified <see cref="MemberInfo"/>.
    /// </summary>
    internal static FieldType? CreateField(IGraphType graphType, MemberInfo memberInfo, Func<MemberInfo, TypeInformation> getTypeInformation, Action<FieldType, MemberInfo>? buildFieldType, bool isInputType)
    {
        var typeInformation = getTypeInformation(memberInfo);
        var fieldGraphType = typeInformation.ConstructGraphType();
        var fieldType = CreateField(memberInfo, fieldGraphType, isInputType);
        // set resolver, if applicable
        buildFieldType?.Invoke(fieldType, memberInfo);
        // apply field attributes after resolver has been set
        ApplyFieldAttributes(graphType, memberInfo, fieldType, isInputType, out var ignore);
        return ignore ? null : fieldType;
    }

    /// <summary>
    /// Creates a <see cref="FieldType"/> for the specified <see cref="MemberInfo"/>.
    /// </summary>
    internal static FieldType CreateField(MemberInfo memberInfo, Type graphType, bool isInputType)
    {
        var fieldType = new FieldType()
        {
            Name = memberInfo.Name,
            Description = memberInfo.Description(),
            DeprecationReason = memberInfo.ObsoleteMessage(),
            Type = graphType,
            DefaultValue = isInputType ? memberInfo.DefaultValue() : null,
        };
        if (isInputType)
        {
            fieldType.WithMetadata(ComplexGraphType<object>.ORIGINAL_EXPRESSION_PROPERTY_NAME, memberInfo.Name);
            var memberType = memberInfo is PropertyInfo propertyInfo ? propertyInfo.PropertyType : ((FieldInfo)memberInfo).FieldType;
            fieldType.Parser = value => memberType.IsInstanceOfType(value) ? value : value.GetPropertyValue(memberType, fieldType.ResolvedType!)!;
        }
        if (!isInputType &&
            memberInfo is MethodInfo methodInfo &&
            fieldType.Name.EndsWith("Async") &&
            methodInfo.ReturnType.IsGenericType &&
            methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            fieldType.Name = fieldType.Name.Substring(0, fieldType.Name.Length - 5);
        }

        return fieldType;
    }

    /// <summary>
    /// Applies <see cref="GraphQLAttribute"/>s defined on <paramref name="memberInfo"/> to <paramref name="fieldType"/>.
    /// Also scans the member's owning module and assembly for globally-applied attributes.
    /// </summary>
    internal static void ApplyFieldAttributes(IGraphType graphType, MemberInfo memberInfo, FieldType fieldType, bool isInputType, out bool ignore)
    {
        // Apply derivatives of GraphQLAttribute
        var attributes = memberInfo.GetGraphQLAttributes();
        ignore = false;
        foreach (var attr in attributes)
        {
            attr.Modify(fieldType, isInputType);
            attr.Modify(fieldType, isInputType, graphType, memberInfo, ref ignore);
        }
    }

    private static string GetPropertyName<TSourceType>(Expression<Func<TSourceType, object?>> expression)
    {
        if (expression.Body is MemberExpression m1)
            return m1.Member.Name;

        if (expression.Body is UnaryExpression u && u.Operand is MemberExpression m2)
            return m2.Member.Name;

        throw new NotSupportedException($"Unsupported type of expression: {expression.GetType().Name}");
    }

    /// <summary>
    /// Constructs a lambda expression for a field resolver to return the specified query argument
    /// from the resolve context. The returned lambda is similar to the following:
    /// <code>context =&gt; context.GetArgument&lt;T&gt;(queryArgument.Name, queryArgument.DefaultValue)</code>
    /// </summary>
    internal static LambdaExpression GetParameterExpression(Type parameterType, QueryArgument queryArgument)
    {
        //construct a typed call to AutoRegisteringHelper.GetArgumentInternal, passing in queryArgument
        var getArgumentMethodTyped = _getArgumentMethod.MakeGenericMethod(parameterType);
        var resolveFieldContextParameter = Expression.Parameter(typeof(IResolveFieldContext), "context");
        var queryArgumentExpression = Expression.Constant(queryArgument, typeof(QueryArgument));
        //e.g. Func<IResolveFieldContext, int> = (context) => AutoRegisteringHelper.GetArgumentInternal<int>(context, queryArgument);
        var expr = Expression.Call(getArgumentMethodTyped, resolveFieldContextParameter, queryArgumentExpression);
        return Expression.Lambda(expr, resolveFieldContextParameter);
    }

    private static readonly MethodInfo _getArgumentMethod = typeof(AutoRegisteringHelper).GetMethod(nameof(GetArgumentInternal), BindingFlags.NonPublic | BindingFlags.Static)!;
    /// <summary>
    /// Returns the value for the specified query argument, or <c>default(T)</c> if the argument
    /// was not specified and the argument is not configured with a default value.
    /// </summary>
    private static T? GetArgumentInternal<T>(IResolveFieldContext context, QueryArgument queryArgument)
    {
        // note: if the query argument has a default value, TryGetArgumentExact will always return true
        return context.TryGetArgumentExact(typeof(T), queryArgument.Name, out var value)
            ? (T?)value
            : default;
    }

    /// <summary>
    /// Returns a list of <see cref="FieldType"/> instances representing the fields ready to be
    /// added to the graph type.
    /// </summary>
    internal static IEnumerable<FieldType> ProvideFields(IEnumerable<MemberInfo> members, Func<MemberInfo, FieldType?> createField, bool isInputType)
    {
        foreach (var memberInfo in members)
        {
            bool include = true;
            foreach (var attr in memberInfo.GetGraphQLAttributes())
            {
                include = attr.ShouldInclude(memberInfo, isInputType);
                if (!include)
                    break;
            }
            if (!include)
                continue;
            var fieldType = createField(memberInfo);
            if (fieldType != null)
                yield return fieldType;
        }
    }

    /// <summary>
    /// Analyzes a member and returns an instance of <see cref="TypeInformation"/>
    /// containing information necessary to select a graph type. Nullable reference annotations
    /// are read, if they exist, as well as the <see cref="RequiredAttribute"/> attribute.
    /// Then any <see cref="GraphQLAttribute"/> attributes marked on the property are applied.
    /// <br/><br/>
    /// Override this method to enforce specific graph types for specific CLR types, or to implement custom
    /// attributes to change graph type selection behavior.
    /// </summary>
    internal static TypeInformation GetTypeInformation(MemberInfo memberInfo, bool isInputType)
    {
        var typeInformation = memberInfo switch
        {
            PropertyInfo propertyInfo => new TypeInformation(propertyInfo, isInputType),
            MethodInfo methodInfo when !isInputType => new TypeInformation(methodInfo),
            FieldInfo fieldInfo => new TypeInformation(fieldInfo, isInputType),
            _ => isInputType
                ? throw new ArgumentOutOfRangeException(nameof(memberInfo), "Only properties and fields are supported.")
                : throw new ArgumentOutOfRangeException(nameof(memberInfo), "Only properties, methods and fields are supported."),
        };
        typeInformation.ApplyAttributes();
        return typeInformation;
    }

    /// <summary>
    /// Analyzes a method argument and returns an instance of <see cref="TypeInformation"/>
    /// containing information necessary to select a graph type. Nullable reference annotations
    /// are read, if they exist, as well as the <see cref="RequiredAttribute"/> attribute.
    /// Then any <see cref="GraphQLAttribute"/> attributes marked on the property are applied.
    /// <br/><br/>
    /// Override this method to enforce specific graph types for specific CLR types, or to implement custom
    /// attributes to change graph type selection behavior.
    /// </summary>
    internal static TypeInformation GetTypeInformation(ParameterInfo parameterInfo)
    {
        var typeInformation = new TypeInformation(parameterInfo);
        typeInformation.ApplyAttributes();
        return typeInformation;
    }

    /// <summary>
    /// Identifies the constructor to use when constructing instances of <typeparamref name="TSourceType"/>.
    /// Selects any public constructor marked with <see cref="GraphQLConstructorAttribute"/>, or the public
    /// parameterless constructor, or the only public contructor, or returns <see langword="null"/> otherwise.
    /// </summary>
    internal static ConstructorInfo? GetConstructorOrDefault<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSourceType>()
        => GetConstructorOrDefault(typeof(TSourceType));

    /// <summary>
    /// Identifies the constructor to use when constructing instances of <paramref name="sourceType"/>.
    /// Selects any public constructor marked with <see cref="GraphQLConstructorAttribute"/>, or the public
    /// parameterless constructor, or the only public contructor, or returns <see langword="null"/> otherwise.
    /// </summary>
    internal static ConstructorInfo? GetConstructorOrDefault([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type sourceType)
    {
        var constructors = sourceType.GetConstructors();
        // if there are no public constructors, return null
        if (constructors == null || constructors.Length == 0)
            return null;
        // if there is only one public constructor, return it
        if (constructors.Length == 1)
            return constructors[0];
        // if there are multiple public constructors, return the one marked with GraphQLConstructorAttribute, or the parameterless constructor, or null
        ConstructorInfo? match = null;
        ConstructorInfo? parameterless = null;
        foreach (var constructor in constructors)
        {
            if (constructor.GetCustomAttribute<GraphQLConstructorAttribute>() != null)
            {
                if (match != null)
                    throw new InvalidOperationException($"Multiple constructors marked with {nameof(GraphQLConstructorAttribute)} found on type '{sourceType.GetFriendlyName()}'.");
                match = constructor;
            }
            if (constructor.GetParameters().Length == 0)
            {
                parameterless = constructor;
            }
        }
        return match ?? parameterless;
    }

    /// <summary>
    /// Identifies the constructor to use when constructing instances of <paramref name="sourceType"/>.
    /// Selects any public constructor marked with <see cref="GraphQLConstructorAttribute"/>, or the public
    /// parameterless constructor, or the only public contructor, or throws an exception otherwise.
    /// May return <see langword="null"/> for the implicit public parameterless constructor for structs.
    /// </summary>
    internal static ConstructorInfo? GetConstructor([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type sourceType)
    {
        var ret = GetConstructorOrDefault(sourceType);
        // if there are no valid constructors, throw the proper exception
        if (ret == null)
        {
            if (sourceType.GetConstructors().Length == 0)
            {
                if (sourceType.IsValueType)
                {
                    // structs have an implicit parameterless constructor
                    return null;
                }
                throw new InvalidOperationException($"No public constructors found on CLR type '{sourceType.GetFriendlyName()}'.");
            }
            else
            {
                throw new InvalidOperationException($"CLR type '{sourceType.GetFriendlyName()}' must have a public parameterless constructor, a single constructor, or a public constructor marked with " + nameof(GraphQLConstructorAttribute) + ".");
            }
        }
        return ret;
    }
}
