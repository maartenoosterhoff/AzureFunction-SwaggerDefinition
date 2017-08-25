using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;

namespace mAdcOW.AzureFunction.SwaggerDefinition
{
    public static class Swagger
    {
        const string SwaggerFunctionName = "Swagger";

        [FunctionName(SwaggerFunctionName)]
        [ResponseType(typeof(void))]
        public static async Task<HttpResponseMessage> RunAsync([HttpTrigger(AuthorizationLevel.Function, "get")]HttpRequestMessage req)
        {
            var assembly = Assembly.GetExecutingAssembly();

            dynamic doc = new ExpandoObject();
            doc.swagger = "2.0";
            doc.info = new ExpandoObject();
            doc.info.title = assembly.DefinedTypes.First().Namespace;
            doc.info.version = "1.0.0";
            doc.host = req.RequestUri.Authority;
            doc.basePath = "/";
            doc.schemes = new[] { "https" };
            if (doc.host == "127.0.0.1" || doc.host == "localhost")
            {
                doc.schemes = new[] { "http" };
            }
            doc.definitions = new ExpandoObject();
            doc.paths = GeneratePaths(assembly, doc);
            doc.securityDefinitions = GenerateSecurityDefinitions();

            return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ObjectContent<object>(doc, new JsonMediaTypeFormatter()),

            });
        }

        private static dynamic GenerateSecurityDefinitions()
        {
            dynamic securityDefinitions = new ExpandoObject();
            securityDefinitions.apikeyQuery = new ExpandoObject();
            securityDefinitions.apikeyQuery.type = "apiKey";
            securityDefinitions.apikeyQuery.name = "code";
            securityDefinitions.apikeyQuery.@in = "query";

            // Microsoft Flow import doesn't like two apiKey options, so we leave one out.

            //securityDefinitions.apikeyHeader = new ExpandoObject();
            //securityDefinitions.apikeyHeader.type = "apiKey";
            //securityDefinitions.apikeyHeader.name = "x-functions-key";
            //securityDefinitions.apikeyHeader.@in = "header";
            return securityDefinitions;
        }

        private static dynamic GeneratePaths(Assembly assembly, dynamic doc)
        {
            dynamic paths = new ExpandoObject();
            var methods = assembly.GetTypes()
                .SelectMany(t => t.GetMethods())
                .Where(m => m.GetCustomAttributes(typeof(FunctionNameAttribute), false).Length > 0)
                .ToArray();
            foreach (MethodInfo methodInfo in methods)
            {
                string route = "/api/";

                var functionAttr = (FunctionNameAttribute)methodInfo.GetCustomAttributes(typeof(FunctionNameAttribute), false)
                    .Single();

                if (functionAttr.Name == SwaggerFunctionName) continue;

                HttpTriggerAttribute triggerAttribute = null;
                foreach (ParameterInfo parameter in methodInfo.GetParameters())
                {
                    triggerAttribute = parameter.GetCustomAttributes(typeof(HttpTriggerAttribute), false)
                        .FirstOrDefault() as HttpTriggerAttribute;
                    if (triggerAttribute != null) break;
                }
                if (triggerAttribute == null) continue; // Trigger attribute is required in an Azure function

                if (!string.IsNullOrWhiteSpace(triggerAttribute.Route))
                {
                    route += triggerAttribute.Route;
                }
                else
                {
                    route += functionAttr.Name;
                }

                dynamic path = new ExpandoObject();

                var verbs = triggerAttribute.Methods ?? new[] { "get", "post", "delete", "head", "patch", "put", "options" };
                foreach (string verb in verbs)
                {
                    dynamic operation = new ExpandoObject();
                    operation.operationId = ToTitleCase(functionAttr.Name) + ToTitleCase(verb);
                    operation.produces = new[] { "application/json" };
                    operation.consumes = new[] { "application/json" };
                    operation.parameters = GenerateFunctionParametersSignature(methodInfo, route, doc);

                    // Summary is title
                    operation.summary = GetFunctionName(methodInfo, functionAttr.Name);
                    // Verbose description
                    operation.description = GetFunctionDescription(methodInfo, functionAttr.Name);

                    operation.responses = GenerateResponseParameterSignature(methodInfo, doc);
                    dynamic keyQuery = new ExpandoObject();
                    keyQuery.apikeyQuery = new string[0];
                    operation.security = new ExpandoObject[] { keyQuery };

                    // Microsoft Flow import doesn't like two apiKey options, so we leave one out.
                    //dynamic apikeyHeader = new ExpandoObject();
                    //apikeyHeader.apikeyHeader = new string[0];
                    //operation.security = new ExpandoObject[] { keyQuery, apikeyHeader };

                    AddToExpando(path, verb, operation);
                }
                AddToExpando(paths, route, path);
            }
            return paths;
        }

        /// <summary>
        /// Max 80 characters in summary/title
        /// </summary>
        private static string GetFunctionDescription(MethodInfo methodInfo, string funcName)
        {
            var displayAttr = (DisplayAttribute)methodInfo.GetCustomAttributes(typeof(DisplayAttribute), false)
                .SingleOrDefault();
            return !string.IsNullOrWhiteSpace(displayAttr?.Description) ? displayAttr.Description : $"This function will run {funcName}";
        }

        private static string GetFunctionName(MethodInfo methodInfo, string funcName)
        {
            var displayAttr = (DisplayAttribute)methodInfo.GetCustomAttributes(typeof(DisplayAttribute), false)
                .SingleOrDefault();
            if (!string.IsNullOrWhiteSpace(displayAttr?.Name))
            {
                return displayAttr.Name.Length > 80 ? displayAttr.Name.Substring(0, 80) : displayAttr.Name;
            }
            return $"Run {funcName}";
        }

        private static dynamic GenerateResponseParameterSignature(MethodInfo methodInfo, dynamic doc)
        {
            dynamic responses = new ExpandoObject();
            dynamic responseDef = new ExpandoObject();
            responseDef.description = "OK";

            var returnType = methodInfo.ReturnType;
            if (returnType.IsGenericType)
            {
                var genericReturnType = returnType.GetGenericArguments().FirstOrDefault();
                if (genericReturnType != null)
                {
                    returnType = genericReturnType;
                }
            }
            if (returnType == typeof(HttpResponseMessage))
            {
                var responseTypeAttr = (ResponseTypeAttribute)methodInfo
                    .GetCustomAttributes(typeof(ResponseTypeAttribute), false).FirstOrDefault();
                if (responseTypeAttr != null)
                {
                    returnType = responseTypeAttr.ResponseType;
                }
                else
                {
                    returnType = typeof(void);
                }
            }
            if (returnType != typeof(void))
            {
                responseDef.schema = new ExpandoObject();

                if (returnType.Namespace == "System")
                {
                    SetParameterType(returnType, responseDef.schema, null);
                }
                else
                {
                    AddToExpando(responseDef.schema, "$ref", "#/definitions/" + returnType.Name);
                    AddParameterDefinition((IDictionary<string, object>)doc.definitions, returnType);
                }
            }
            AddToExpando(responses, "200", responseDef);
            return responses;
        }

        private static List<object> GenerateFunctionParametersSignature(MethodInfo methodInfo, string route, dynamic doc)
        {
            var parameterSignatures = new List<object>();
            foreach (ParameterInfo parameter in methodInfo.GetParameters())
            {
                if (parameter.ParameterType == typeof(HttpRequestMessage)) continue;
                if (parameter.ParameterType == typeof(TraceWriter)) continue;

                bool hasUriAttribute = parameter.GetCustomAttributes().Any(attr => attr is FromUriAttribute);


                if (route.Contains('{' + parameter.Name))
                {
                    dynamic opParam = new ExpandoObject();
                    opParam.name = parameter.Name;
                    opParam.@in = "path";
                    opParam.required = true;
                    SetParameterType(parameter.ParameterType, opParam, null);
                    parameterSignatures.Add(opParam);
                }
                else if (hasUriAttribute && parameter.ParameterType.Namespace == "System")
                {
                    dynamic opParam = new ExpandoObject();
                    opParam.name = parameter.Name;
                    opParam.@in = "query";
                    opParam.required = parameter.GetCustomAttributes().Any(attr => attr is RequiredAttribute);
                    SetParameterType(parameter.ParameterType, opParam, doc.definitions);
                    parameterSignatures.Add(opParam);
                }
                else if (hasUriAttribute && parameter.ParameterType.Namespace != "System")
                {
                    AddObjectProperties(parameter.ParameterType, parameter.Name, parameterSignatures, doc);
                }
                else
                {
                    dynamic opParam = new ExpandoObject();
                    opParam.name = parameter.Name;
                    opParam.@in = "body";
                    opParam.required = true;
                    opParam.schema = new ExpandoObject();
                    if (parameter.ParameterType.Namespace == "System")
                    {
                        SetParameterType(parameter.ParameterType, opParam.schema, null);
                    }
                    else
                    {
                        AddToExpando(opParam.schema, "$ref", "#/definitions/" + parameter.ParameterType.Name);
                        AddParameterDefinition((IDictionary<string, object>)doc.definitions, parameter.ParameterType);
                    }
                    parameterSignatures.Add(opParam);
                }
            }
            return parameterSignatures;
        }

        private static void AddObjectProperties(Type t, string parentName, List<object> parameterSignatures, dynamic doc)
        {
            var publicProperties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo property in publicProperties)
            {
                if (property.PropertyType.Namespace != "System")
                {
                    AddObjectProperties(property.PropertyType, parentName + "." + property.Name, parameterSignatures, doc);
                }
                else
                {
                    dynamic opParam = new ExpandoObject();
                    opParam.name = parentName + "." + property.Name;
                    opParam.@in = "query";
                    opParam.required = property.GetCustomAttributes().Any(attr => attr is RequiredAttribute);
                    SetParameterType(property.PropertyType, opParam, doc.definitions);
                    parameterSignatures.Add(opParam);
                }
            }
        }

        private static void AddParameterDefinition(IDictionary<string, object> definitions, Type parameterType)
        {
            dynamic objDef;
            if (!definitions.TryGetValue(parameterType.Name, out objDef))
            {
                objDef = new ExpandoObject();
                objDef.type = "object";
                objDef.properties = new ExpandoObject();
                var publicProperties = parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (PropertyInfo property in publicProperties)
                {
                    dynamic propDef = new ExpandoObject();
                    SetParameterType(property.PropertyType, propDef, definitions);
                    AddToExpando(objDef.properties, property.Name, propDef);
                }
                definitions.Add(parameterType.Name, objDef);
            }
        }

        private static void SetParameterType(Type parameterType, dynamic opParam, dynamic definitions)
        {
            if (parameterType.Namespace == "System")
            {
                switch (Type.GetTypeCode(parameterType))
                {
                    case TypeCode.Int32:
                        opParam.format = "int32";
                        opParam.type = "integer";
                        break;
                    case TypeCode.Int64:
                        opParam.format = "int64";
                        opParam.type = "integer";
                        break;
                    case TypeCode.Single:
                        opParam.format = "float";
                        opParam.type = "number";
                        break;
                    case TypeCode.Double:
                        opParam.format = "double";
                        opParam.type = "number";
                        break;
                    case TypeCode.String:
                        opParam.type = "string";
                        break;
                    case TypeCode.Byte:
                        opParam.format = "byte";
                        opParam.type = "string";
                        break;
                    case TypeCode.Boolean:
                        opParam.type = "boolean";
                        break;
                    case TypeCode.DateTime:
                        opParam.format = "date";
                        opParam.type = "string";
                        break;
                    default:
                        opParam.type = "string";
                        break;
                }
            }
            else if (definitions != null)
            {
                AddToExpando(opParam, "$ref", "#/definitions/" + parameterType.Name);
                AddParameterDefinition((IDictionary<string, object>)definitions, parameterType);
            }
        }

        public static string ToTitleCase(string str)
        {
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(str);
        }

        public static void AddToExpando(ExpandoObject obj, string name, object value)
        {
            ((IDictionary<string, object>)obj).Add(name, value);
        }
    }
}