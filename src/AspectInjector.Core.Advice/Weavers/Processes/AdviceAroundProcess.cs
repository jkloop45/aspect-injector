﻿using AspectInjector.Core.Advice.Effects;
using AspectInjector.Core.Contracts;
using AspectInjector.Core.Extensions;
using AspectInjector.Core.Fluent;
using AspectInjector.Core.Models;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AspectInjector.Core.Advice.Weavers.Processes
{
    internal class AdviceAroundProcess : AdviceWeaveProcessBase<AroundAdviceEffect>
    {
        private readonly string _wrapperNamePrefix;
        private readonly string _unWrapperName;
        private readonly string _movedOriginalName;
        private MethodDefinition _wrapper;

        public AdviceAroundProcess(ILogger log, AspectDefinition aspect, MethodDefinition target, AroundAdviceEffect effect) : base(log, target, effect, aspect)
        {
            _wrapperNamePrefix = $"{GetAroundMethodPrefix(_target)}w_";
            _unWrapperName = $"{GetAroundMethodPrefix(_target)}u";
            _movedOriginalName = $"{GetAroundMethodPrefix(_target)}o";
        }

        public override void Execute()
        {
            _wrapper = GetNextWrapper();

            _wrapper.GetEditor().Instead(
                e => e
                .LoadAspect(_aspect)
                .Call(_effect.Method, LoadAdviceArgs)
                .Return()
            );
        }

        protected override void LoadTargetArgument(PointCut pc, AdviceArgument parameter)
        {
            var targetFuncType = _ts.MakeGenericInstanceType(
                _ts.FuncGeneric2,
                _ts.ObjectArray,
                _ts.Object);

            var targetFuncCtor = targetFuncType.Resolve().Methods.First(m => m.IsConstructor && !m.IsStatic).MakeHostInstanceGeneric(targetFuncType);

            pc.ThisOrNull().Call(targetFuncCtor, args => args.Load(_wrapper.ParametrizeGenericChild(GetOrCreateUnwrapper())));
        }

        protected override void LoadArgumentsArgument(PointCut pc, AdviceArgument parameter)
        {
            pc.Load(_wrapper.Parameters[0]);
        }

        public MethodDefinition GetNextWrapper()
        {
            var wrapper = _target.DeclaringType.Methods.Where(m => m.Name.StartsWith(_wrapperNamePrefix))
                .Select(m => new { m, i = ushort.Parse(m.Name.Substring(_wrapperNamePrefix.Length)) }).OrderByDescending(g => g.i).FirstOrDefault();

            return ReplaceUnwrapper($"{_wrapperNamePrefix}{(wrapper == null ? 0 : wrapper.i + 1)}");
        }

        private MethodDefinition ReplaceUnwrapper(string name)
        {
            var unwrapper = GetOrCreateUnwrapper();
            var newUnwrapper = DuplicateMethodDefinition(unwrapper);
            newUnwrapper.IsPrivate = true;

            unwrapper.Name = name;

            MoveBody(unwrapper, newUnwrapper);

            return unwrapper;
        }

        private MethodDefinition GetOrCreateUnwrapper()
        {
            var unwrapper = _target.DeclaringType.Methods.FirstOrDefault(m => m.Name == _unWrapperName);
            if (unwrapper != null)
                return unwrapper;

            unwrapper = DuplicateMethodDefinition(_target);
            unwrapper.Name = _unWrapperName;
            unwrapper.ReturnType = _ts.Object;
            unwrapper.IsPrivate = true;
            unwrapper.Parameters.Clear();
            var argsParam = new ParameterDefinition(_ts.ObjectArray);
            unwrapper.Parameters.Add(argsParam);
            unwrapper.Body.InitLocals = true;

            var original = WrapEntryPoint(unwrapper);

            unwrapper.GetEditor().Instead(
                il =>
                {
                    var refList = new List<Tuple<int, VariableDefinition>>();

                    il = il.ThisOrStatic().Call(original, c =>
                    {
                        for (int i = 0; i < original.Parameters.Count; i++)
                        {
                            var p = original.Parameters[i];

                            if (p.ParameterType.IsByReference)
                            {
                                var elementType = ((ByReferenceType)p.ParameterType).ElementType;

                                var tempVar = new VariableDefinition($"{Constants.Prefix}p_{p.Name}", elementType);
                                refList.Add(new Tuple<int, VariableDefinition>(i, tempVar));
                                unwrapper.Body.Variables.Add(tempVar);

                                c.Store(tempVar, v => v.Load(argsParam).GetByIndex(i).Cast(elementType));
                                c.LoadRef(tempVar);
                            }
                            else
                            {
                                c = c.Load(argsParam).GetByIndex(i);

                                if (p.ParameterType.IsGenericParameter || !p.ParameterType.IsTypeOf(_ts.Object))
                                    c = c.Cast(p.ParameterType);
                            }
                        }
                    });

                    foreach (var refPar in refList)
                        il = il.Load(argsParam).SetByIndex(refPar.Item1, val => val.Load(refPar.Item2).ByVal(refPar.Item2.VariableType));

                    if (original.ReturnType.IsTypeOf(_ts.Void))
                        il = il.Value((object)null);
                    else if (original.ReturnType.IsValueType || original.ReturnType.IsGenericParameter)
                        il = il.ByVal(original.ReturnType);

                    il.Return();
                });

            return unwrapper;
        }

        private MethodDefinition WrapEntryPoint(MethodDefinition unwrapper)
        {
            var original = DuplicateMethodDefinition(_target);
            original.Name = _movedOriginalName;
            original.IsPrivate = true;

            var returnType = _target.ResolveGenericType(_target.ReturnType);

            MoveBody(_target, original);

            _target.GetEditor().Instead(
                e =>
                {
                    //var args = null;
                    var argsVar = new VariableDefinition(_ts.ObjectArray);
                    _target.Body.Variables.Add(argsVar);
                    _target.Body.InitLocals = true;

                    //args = new object[] { param1, param2 ...};
                    e.Store(argsVar, args => base.LoadArgumentsArgument(args, null));

                    // Unwrapper(args);
                    e.ThisOrStatic().Call(unwrapper, args => args.Load(argsVar));

                    // proxy ref and out params
                    for (int i = 0; i < _target.Parameters.Count; i++)
                    {
                        var p = _target.Parameters[i];
                        if (p.ParameterType.IsByReference)
                            e.StoreByRef(p, val => e.Load(argsVar).GetByIndex(i));
                    }

                    //drop if void, cast if not is object
                    if (returnType.IsTypeOf(_ts.Void))
                        e = e.Pop();
                    else if (!returnType.IsTypeOf(_ts.Object))
                        e = e.Cast(returnType);

                    e.Return();
                });

            return original;
        }

        private void MoveBody(MethodDefinition from, MethodDefinition to)
        {
            foreach (var inst in from.Body.Instructions)
                to.Body.Instructions.Add(inst);

            foreach (var var in from.Body.Variables)
                to.Body.Variables.Add(new VariableDefinition(var.Name, _ts.Import(var.VariableType)));

            if (to.Body.HasVariables)
                to.Body.InitLocals = true;

            foreach (var handler in from.Body.ExceptionHandlers)
                to.Body.ExceptionHandlers.Add(handler);

            //erase old body
            from.Body.Instructions.Clear();
            from.Body = new MethodBody(from);
        }

        private MethodDefinition DuplicateMethodDefinition(MethodDefinition origin)
        {
            var method = new MethodDefinition(origin.Name,
               origin.Attributes,
               origin.ReturnType);

            foreach (var gparam in origin.GenericParameters)
                method.GenericParameters.Add(new GenericParameter(gparam.Name, method));

            if (origin.ReturnType.IsGenericParameter && ((GenericParameter)origin.ReturnType).Owner == origin)
                method.ReturnType = method.GenericParameters[origin.GenericParameters.IndexOf((GenericParameter)origin.ReturnType)];

            if (origin.IsSpecialName)
                method.IsSpecialName = true;

            foreach (var parameter in origin.Parameters)
            {
                var paramType = parameter.ParameterType;
                if (paramType.IsGenericParameter && ((GenericParameter)paramType).Owner == origin)
                    paramType = method.GenericParameters[origin.GenericParameters.IndexOf((GenericParameter)paramType)];

                method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, paramType));
            }

            origin.DeclaringType.Methods.Add(method);

            return method;
        }

        private static string GetAroundMethodPrefix(MethodDefinition target)
        {
            return $"{Constants.Prefix}around_{target.Name}_{target.MetadataToken.ToUInt32()}_";
        }
    }
}