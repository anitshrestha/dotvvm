﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using DotVVM.Framework.Binding.Expressions;
using DotVVM.Framework.Binding.Properties;
using DotVVM.Framework.Compilation;
using DotVVM.Framework.Compilation.Binding;
using DotVVM.Framework.Compilation.ControlTree.Resolved;
using DotVVM.Framework.Controls;

namespace DotVVM.Framework.Binding
{
    public class BindingCompilationService
    {
        public BindingCompilationService(params object[] transformers)
        {
            resolvers.AddResolver(new Func<BindingAdditionalResolvers, BindingResolverCollection>(
                rr => new BindingResolverCollection(rr.Resolvers)));
            foreach (var p in
                from t in transformers
                from m in t.GetType().GetMethods()
                select m.CreateDelegate(MethodGroupExpression.GetDelegateType(m), t)
            )
                resolvers.AddDelegate(p);
        }

        BindingResolverCollection resolvers = new BindingResolverCollection();

        public object ComputeProperty(Type type, IBinding binding)
        {
            var typeName = type.ToString();
            var additionalResolvers = type != typeof(BindingAdditionalResolvers) && type != typeof(BindingResolverCollection) ? binding.GetProperty<BindingResolverCollection>(ErrorHandlingMode.ReturnNull) : null;
            var bindingResolvers = GetResolversForBinding(binding.GetType());

            var resolver = additionalResolvers?.FindResolver(type) ??
                bindingResolvers?.FindResolver(type) ??
                this.resolvers.FindResolver(type);

            object getParameterValue(ParameterInfo p) => binding.GetProperty(p.ParameterType, p.HasDefaultValue ? ErrorHandlingMode.ReturnNull : ErrorHandlingMode.ReturnException) ?? p.DefaultValue;

            Exception checkArguments(object[] arguments) =>
                arguments.OfType<Exception>().ToArray() is var exceptions && exceptions.Any() ?
                new AggregateException($"Could not resolve '{type}'.", exceptions) :
                null;

            if (resolver != null)
            {
                var arguments = resolver.GetMethodInfo().GetParameters().Select(getParameterValue).ToArray();
                { if (checkArguments(arguments) is Exception exc) return exc; }
                var value = resolver.DynamicInvoke(arguments);
                // post process the value
                foreach (var postProcessor in this.resolvers.GetPostProcessors(type)
                    .Concat(bindingResolvers.GetPostProcessors(type)
                    .Concat(additionalResolvers?.GetPostProcessors(type) ?? Enumerable.Empty<Delegate>())))
                {
                    var method = postProcessor.GetMethodInfo();
                    arguments = new[] { value }.Concat(method.GetParameters().Skip(1).Select(getParameterValue)).ToArray();
                    if (checkArguments(arguments) is Exception exc) return exc;
                    value = postProcessor.DynamicInvoke(arguments) ?? value;
                }
                return value;
            }
            else return new InvalidOperationException($"Could not resolve binding property '{type}'."); // don't throw the exception, since it creates noise for debugger
        }

        protected Exception GetException(IBinding binding, string message) =>
            binding.GetProperty<ResolvedBinding>(ErrorHandlingMode.ReturnNull) is ResolvedBinding resolvedBinding ?
                new DotvvmCompilationException(message, resolvedBinding.DothtmlNode.Tokens) :
            binding.GetProperty<LocationInfoBindingProperty>(ErrorHandlingMode.ReturnNull) is LocationInfoBindingProperty locationInfo ?
                new DotvvmControlException(message, null, locationInfo.ControlType, locationInfo.LineNumber, locationInfo.FileName, locationInfo.Ranges) :
            new Exception(null);

        ConcurrentDictionary<Type, BindingResolverCollection> bindingResolverCache = new ConcurrentDictionary<Type, BindingResolverCollection>();
        BindingResolverCollection GetResolversForBinding(Type bindingType)
        {
            return bindingResolverCache.GetOrAdd(bindingType, t =>
                new BindingResolverCollection(t.GetTypeInfo().GetCustomAttributes<BindingCompilationOptionsAttribute>(true)
                .SelectMany(o => o.GetResolvers())));
        }

        //struct PropertyResolver
        //{
        //    public readonly Delegate Func;
        //    public readonly List<Delegate> PostProcs;

        //    public PropertyResolver(Delegate func, IEnumerable<Delegate> postProcs = null)
        //    {
        //        this.Func = func;
        //        this.PostProcs = postProcs?.ToList() ?? new List<Delegate>();
        //    }
        //}

        class BindingResolverCollection
        {
            private readonly ConcurrentDictionary<Type, Delegate> resolvers = new ConcurrentDictionary<Type, Delegate>();
            private readonly ConcurrentDictionary<Type, ConcurrentStack<Delegate>> postProcs = new ConcurrentDictionary<Type, ConcurrentStack<Delegate>>();

            public BindingResolverCollection() { }

            public BindingResolverCollection(IEnumerable<Delegate> delegates)
            {
                foreach (var d in delegates) AddDelegate(d, replace: true);
            }

            public void AddResolver(Delegate resolver, bool replace = false)
            {
                if (replace) resolvers[resolver.GetMethodInfo().ReturnType] = resolver;
                else if (!resolvers.TryAdd(resolver.GetMethodInfo().ReturnType, resolver))
                    throw new NotSupportedException($"Can't insert more resolvers for property of type '{resolver.GetMethodInfo().ReturnType}'.");
            }

            public void AddPostProcessor(Delegate processor)
            {
                var method = processor.GetMethodInfo();
                var type = method.GetParameters().First().ParameterType;
                if (method.ReturnType != typeof(void) || method.ReturnType != type)
                    throw new Exception("Binding property post-processing function must return void or first parameter's type.");
                var list = postProcs.GetOrAdd(type, _ => new ConcurrentStack<Delegate>());
                list.Push(processor);
            }

            public void AddDelegate(Delegate func, bool replace = false)
            {
                var method = func.GetMethodInfo();
                var type = method.GetParameters().FirstOrDefault()?.ParameterType;
                if (method.ReturnType == typeof(void) || method.ReturnType == type)
                    AddPostProcessor(func);
                else AddResolver(func, replace);
            }

            public IEnumerable<Delegate> GetPostProcessors(Type type) =>
                postProcs.TryGetValue(type, out var result) ? result : Enumerable.Empty<Delegate>();

            public Delegate FindResolver(Type type) =>
                resolvers.TryGetValue(type, out var result) ? result : null;
        }

        ConcurrentDictionary<Type, BindingCompilationRequirementsAttribute> defaultRequirementCache = new ConcurrentDictionary<Type, BindingCompilationRequirementsAttribute>();
        protected BindingCompilationRequirementsAttribute GetDefaultRequirements(Type bindingType)
        {
            return defaultRequirementCache.GetOrAdd(bindingType, t =>
                t.GetTypeInfo().GetCustomAttributes<BindingCompilationRequirementsAttribute>(inherit: true).Aggregate((a, b) => a.ApplySecond(b)));
        }

        public BindingCompilationRequirementsAttribute GetRequirements(IBinding binding, IEnumerable<BindingCompilationRequirementsAttribute> bindingRequirements = null)
        {
            var requirements = GetDefaultRequirements(binding.GetType());
            if (bindingRequirements != null) foreach (var req in bindingRequirements) requirements = requirements.ApplySecond(req);
            if (binding.GetProperty<BindingCompilationRequirementsAttribute>(ErrorHandlingMode.ReturnNull) is var second && second != null)
                requirements = requirements.ApplySecond(second);
            return requirements;
        }

        /// <summary>
        /// Resolves required and optional properties
        /// </summary>
        public void InitializeBinding(IBinding binding, IEnumerable<BindingCompilationRequirementsAttribute> bindingRequirements = null)
        {
            InitializeBinding(binding, GetRequirements(binding, bindingRequirements));
        }

        protected static void InitializeBinding(IBinding binding, BindingCompilationRequirementsAttribute bindingRequirements)
        {
            var errors = new List<(Type req, Exception error)>();
            foreach (var item in bindingRequirements.Required)
            {
                try
                {
                    binding.GetProperty(item);
                }
                catch (Exception exception)
                {
                    errors.Add((item, exception));
                }
            }
            if (errors.Any()) throw new AggregateException($"Could not initialize binding '{binding.GetType()}', requirements {string.Join(", ", errors.Select(e => e.req))} was not met", errors.Select(e => e.error));
            foreach (var req in bindingRequirements.Optional)
            {
                binding.GetProperty(req, ErrorHandlingMode.ReturnNull);
            }
        }
    }
}
