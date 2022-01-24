using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.DataGateway.Service.Models;
using Azure.DataGateway.Service.Resolvers;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;

namespace Azure.DataGateway.Services
{
    /// <summary>
    /// The resolver middleware that is used by the schema executor to resolve
    /// the queries and mutations
    /// </summary>
    public class ResolverMiddleware
    {
        private static readonly string _contextMetadata = "metadata";
        internal readonly FieldDelegate _next;
        internal readonly IQueryEngine _queryEngine;
        internal readonly IMutationEngine _mutationEngine;
        internal readonly IMetadataStoreProvider _metadataStoreProvider;

        public ResolverMiddleware(FieldDelegate next,
            IQueryEngine queryEngine,
            IMutationEngine mutationEngine,
            IMetadataStoreProvider metadataStoreProvider)
        {
            _next = next;
            _queryEngine = queryEngine;
            _mutationEngine = mutationEngine;
            _metadataStoreProvider = metadataStoreProvider;
        }

        public ResolverMiddleware(FieldDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(IMiddlewareContext context)
        {
            JsonElement jsonElement;

            if (context.Selection.Field.Coordinate.TypeName.Value == "Mutation")
            {
                IDictionary<string, object> parameters = GetParametersFromContext(context);

                Tuple<JsonDocument, IMetadata> result = await _mutationEngine.ExecuteAsync(context, parameters);
                context.Result = result.Item1;
                SetNewMetadata(context, result.Item2);
            }
            else if (context.Selection.Field.Coordinate.TypeName.Value == "Query")
            {
                IDictionary<string, object> parameters = GetParametersFromContext(context);

                if (context.Selection.Type.IsListType())
                {
                    Tuple<IEnumerable<JsonDocument>, IMetadata> result = await _queryEngine.ExecuteListAsync(context, parameters);
                    context.Result = result.Item1;
                    SetNewMetadata(context, result.Item2);
                }
                else
                {
                    bool isPaginatedQuery = _queryEngine.IsPaginatedQuery(context.Selection.Field.Name.Value);
                    Tuple<JsonDocument, IMetadata> result = await _queryEngine.ExecuteAsync(context, parameters, isPaginatedQuery);
                    context.Result = result.Item1;
                    SetNewMetadata(context, result.Item2);
                }
            }
            else if (context.Selection.Field.Type.IsLeafType())
            {
                // This means this field is a scalar, so we don't need to do
                // anything for it.
                if (TryGetPropertyFromParent(context, out jsonElement))
                {
                    context.Result = jsonElement.ToString();
                }
            }
            else if (IsInnerObject(context))
            {
                // This means it's a field that has another custom type as its
                // type, so there is a full JSON object inside this key. For
                // example such a JSON object could have been created by a
                // One-To-Many join.
                if (TryGetPropertyFromParent(context, out jsonElement))
                {
                    IMetadata metadata = GetMetadata(context);
                    context.Result = _queryEngine.ResolveInnerObject(jsonElement, context.Selection.Field, ref metadata);
                    SetNewMetadata(context, metadata);
                }
            }
            else if (context.Selection.Type.IsListType())
            {
                // This means the field is a list and HotChocolate requires
                // that to be returned as a List of JsonDocuments. For example
                // such a JSON list could have been created by a One-To-Many
                // join.
                if (TryGetPropertyFromParent(context, out jsonElement))
                {
                    IMetadata metadata = GetMetadata(context);
                    context.Result = _queryEngine.ResolveListType(jsonElement, context.Selection.Field, ref metadata);
                    SetNewMetadata(context, metadata);
                }
            }

            await _next(context);
        }

        protected static bool TryGetPropertyFromParent(IMiddlewareContext context, out JsonElement jsonElement)
        {
            JsonDocument result = context.Parent<JsonDocument>();
            if (result == null)
            {
                jsonElement = default;
                return false;
            }

            return result.RootElement.TryGetProperty(context.Selection.Field.Name.Value, out jsonElement);
        }

        protected static bool IsInnerObject(IMiddlewareContext context)
        {
            return context.Selection.Field.Type.IsObjectType() && context.Parent<JsonDocument>() != default;
        }

        static private object ArgumentValue(IValueNode value)
        {
            if (value.Kind == SyntaxKind.IntValue)
            {
                IntValueNode intValue = (IntValueNode)value;
                return intValue.ToInt64();
            }
            else
            {
                return value.Value;
            }
        }

        /// <summary>
        /// Extract parameters from the schema and the actual instance (query) of the field
        /// Extracts defualt parameter values from the schema or null if no default
        /// Overrides default values with actual values of parameters provided
        /// </summary>
        public static IDictionary<string, object> GetParametersFromSchemaAndQueryFields(IObjectField schema, FieldNode query)
        {
            IDictionary<string, object> parameters = new Dictionary<string, object>();

            // Fill the parameters dictionary with the default argument values
            IFieldCollection<IInputField> availableArguments = schema.Arguments;
            foreach (IInputField argument in availableArguments)
            {
                if (argument.DefaultValue == null)
                {
                    parameters.Add(argument.Name.Value, null);
                }
                else
                {
                    parameters.Add(argument.Name.Value, ArgumentValue(argument.DefaultValue));
                }
            }

            // Overwrite the default values with the passed in arguments
            IReadOnlyList<ArgumentNode> passedArguments = query.Arguments;
            foreach (ArgumentNode argument in passedArguments)
            {
                parameters[argument.Name.Value] = ArgumentValue(argument.Value);
            }

            return parameters;
        }

        protected static IDictionary<string, object> GetParametersFromContext(IMiddlewareContext context)
        {
            return GetParametersFromSchemaAndQueryFields(context.Selection.Field, context.Selection.SyntaxNode);
        }

        /// <summary>
        /// Get metadata from context
        /// </summary>
        private static IMetadata GetMetadata(IMiddlewareContext context)
        {
            return (IMetadata)context.ScopedContextData[_contextMetadata];
        }

        /// <summary>
        /// Set new metadata and reset the depth that the metadata has persisted
        /// </summary>
        private static void SetNewMetadata(IMiddlewareContext context, IMetadata metadata)
        {
            context.ScopedContextData = context.ScopedContextData.SetItem(_contextMetadata, metadata);
        }
    }
}