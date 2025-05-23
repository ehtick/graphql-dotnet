using GraphQL.Federation;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Microsoft.Extensions.DependencyInjection;

namespace GraphQL.Tests.Federation;

public class InterfaceEntityTests
{
    [Fact]
    public async Task SchemaFirst_Test()
    {
        // Define the schema with Media interface and Book type
        var sdlInput = """
            interface Media @key(fields: "id") {
              id: ID!
              title: String!
            }

            type Book implements Media @key(fields: "id") {
              id: ID!
              title: String!
            }
            """;

        // Create and initialize the schema
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddGraphQL(x => x
            .AddSchema(provider =>
            {
                var schema = Schema.For(sdlInput, c =>
                {
                    c.ServiceProvider = provider;
                    c.Types.For("Book").IsTypeOf<Book>();
                    c.Types.For("Book").ResolveReference<Book>((ctx, rep) =>
                        new Book { Id = rep.Id, Title = $"Book {rep.Id}" });
                    c.Types.For("Media").ResolveReference<Media>((ctx, rep) =>
                        new Book { Id = rep.Id, Title = $"Book {rep.Id}" });
                });
                schema.Query = new ObjectGraphType() { Name = "Query" };
                return schema;
            })
            .AddFederation("2.3", c => c.Imports["@interfaceObject"] = "@interfaceObject"));

        var schema = serviceCollection.BuildServiceProvider().GetRequiredService<ISchema>();
        await ValidateAsync(schema);
    }

    [Fact]
    public async Task CodeFirst_Test()
    {
        // Create interface type
        var mediaInterface = new InterfaceGraphType<IMedia>
        {
            Name = "Media"
        };
        var idField = mediaInterface.Field(m => m.Id, typeof(NonNullGraphType<IdGraphType>));
        var titleField = mediaInterface.Field(m => m.Title, typeof(NonNullGraphType<StringGraphType>));
        mediaInterface.Key("id");
        mediaInterface.ResolveReference<Media>((ctx, rep) => new Book { Id = rep.Id, Title = $"Book {rep.Id}" });

        // Create Book type implementing the interface
        var bookType = new ObjectGraphType<Book>
        {
            Name = "Book"
        };
        var bookIdField = bookType.Field(b => b.Id, typeof(NonNullGraphType<IdGraphType>));
        var bookTitleField = bookType.Field(b => b.Title, typeof(NonNullGraphType<StringGraphType>));
        bookType.Key("id");
        bookType.ResolveReference((ctx, rep) => new Book { Id = rep.Id, Title = $"Book {rep.Id}" });
        bookType.AddResolvedInterface(mediaInterface);

        // Create query type
        var queryType = new ObjectGraphType { Name = "Query" };

        // Create and initialize the schema
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddGraphQL(x => x
            .AddSchema(provider => new Schema(provider) { Query = queryType })
            .AddFederation("2.3", c => c.Imports["@interfaceObject"] = "@interfaceObject")
            .ConfigureSchema(s =>
            {
                s.RegisterType(mediaInterface);
                s.RegisterType(bookType);
            }));

        var schema = serviceCollection.BuildServiceProvider().GetRequiredService<ISchema>();
        await ValidateAsync(schema);
    }

#if NET6_0_OR_GREATER // Default interface implementations (required for [FederationResolver] on an interface) require .NET 6.0 or greater
    [Fact]
    public async Task TypeFirst_Test()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddGraphQL(b => b
            .AddAutoSchema<Query>()
            .AddFederation("2.3", c => c.Imports["@interfaceObject"] = "@interfaceObject")
            .ConfigureSchema(s => s.RegisterType(typeof(AutoRegisteringObjectGraphType<Book>))));
        var schema = serviceCollection.BuildServiceProvider().GetRequiredService<ISchema>();
        await ValidateAsync(schema);
    }
#endif

    private async Task ValidateAsync(ISchema schema)
    {
        schema.Initialize();

        // Verify the schema was created correctly
        var sdl = schema.Print(new() { StringComparison = StringComparison.OrdinalIgnoreCase });
        sdl.ShouldBe(approvedSdl);

        // Execute the query
        var query = """
            query {
              _entities(representations: [{ __typename: "Book", id: "1" }]) {
                ... on Media {
                  __typename
                  id
                  title
                }
              }
            }
            """;

        var executor = new DocumentExecuter();
        var result = await executor.ExecuteAsync(new ExecutionOptions
        {
            Schema = schema,
            Query = query
        });

        // Verify the result
        var resultJson = new GraphQLSerializer().Serialize(result);
        resultJson.ShouldBe("""
            {"data":{"_entities":[{"__typename":"Book","id":"1","title":"Book 1"}]}}
            """);

        // Execute the query with Media interface
        var interfaceQuery = """
            query {
              _entities(representations: [{ __typename: "Media", id: "1" }]) {
                ... on Media {
                  __typename
                  id
                  title
                }
              }
            }
            """;

        var interfaceResult = await executor.ExecuteAsync(new ExecutionOptions
        {
            Schema = schema,
            Query = interfaceQuery
        });

        // Verify the result - this should return null for the entity since Media is an interface
        var interfaceResultJson = new GraphQLSerializer().Serialize(interfaceResult);
        interfaceResultJson.ShouldBe("""
            {"data":{"_entities":[{"__typename":"Book","id":"1","title":"Book 1"}]}}
            """);
    }

    private const string approvedSdl = """
        schema @link(import: ["@link"], url: "https://specs.apollo.dev/link/v1.0") @link(import: ["@key", "@external", "@requires", "@provides", "@shareable", "@inaccessible", "@override", "@tag", "@interfaceObject"], url: "https://specs.apollo.dev/federation/v2.3") {
          query: Query
        }

        directive @external on FIELD_DEFINITION | OBJECT

        directive @federation__composeDirective(name: String!) repeatable on SCHEMA

        directive @federation__extends on INTERFACE | OBJECT

        directive @inaccessible on ARGUMENT_DEFINITION | ENUM | ENUM_VALUE | FIELD_DEFINITION | INPUT_FIELD_DEFINITION | INPUT_OBJECT | INTERFACE | OBJECT | SCALAR | UNION

        directive @interfaceObject on OBJECT

        directive @key(fields: federation__FieldSet!, resolvable: Boolean = true) repeatable on INTERFACE | OBJECT

        directive @link(as: String, import: [link__Import], purpose: link__Purpose, url: String!) repeatable on SCHEMA

        directive @override(from: String!) on FIELD_DEFINITION

        directive @provides(fields: federation__FieldSet!) on FIELD_DEFINITION

        directive @requires(fields: federation__FieldSet!) on FIELD_DEFINITION

        directive @shareable repeatable on FIELD_DEFINITION | OBJECT

        directive @tag(name: String!) repeatable on ARGUMENT_DEFINITION | ENUM | ENUM_VALUE | FIELD_DEFINITION | INPUT_FIELD_DEFINITION | INPUT_OBJECT | INTERFACE | OBJECT | SCALAR | SCHEMA | UNION

        type Book implements Media @key(fields: "id") {
          id: ID!
          title: String!
        }

        scalar federation__FieldSet

        scalar link__Import

        enum link__Purpose {
          "`EXECUTION` features provide metadata necessary for operation execution."
          EXECUTION
          "`SECURITY` features provide metadata necessary to securely resolve fields."
          SECURITY
        }

        interface Media @key(fields: "id") {
          id: ID!
          title: String!
        }

        type Query {
          _entities(representations: [_Any!]!): [_Entity]!
          _service: _Service!
        }

        scalar _Any

        union _Entity = Book

        type _Service {
          sdl: String
        }

        """;

    public class Query
    {
    }

    // Model classes
    [Name("Media")] // used for type-first only
    [Key("id")] // used for type-first only
    public interface IMedia
    {
        [Id] // used for type-first only
        public string Id { get; set; }
        public string Title { get; set; }

#if NET6_0_OR_GREATER // Default interface implementations (required for [FederationResolver] on an interface) require .NET 6.0 or greater
        [FederationResolver]
        public static IMedia ResolveReference(string id) // used for type-first only
        {
            return new Book { Id = id, Title = $"Book {id}" };
        }
#endif
    }

    public class Media : IMedia
    {
        [Id] // used for type-first only
        public string Id { get; set; }
        public string Title { get; set; }
    }

    [Implements(typeof(IMedia))] // used for type-first only
    [Key("id")] // used for type-first only
    public class Book : Media
    {
        [FederationResolver]
        public static Book ResolveReference(string id) // used for type-first only
        {
            return new Book { Id = id, Title = $"Book {id}" };
        }
    }
}
