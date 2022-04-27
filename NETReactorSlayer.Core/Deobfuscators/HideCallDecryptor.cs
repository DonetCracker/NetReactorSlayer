﻿/*
    Copyright (C) 2021 CodeStrikers.org
    This file is part of NETReactorSlayer.
    NETReactorSlayer is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    NETReactorSlayer is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.
    You should have received a copy of the GNU General Public License
    along with NETReactorSlayer.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace NETReactorSlayer.Core.Deobfuscators;

internal class HideCallDecryptor : IDeobfuscator
{
    private readonly List<MethodDef> _delegateCreatorMethods = new();
    private readonly InstructionEmulator _instrEmulator = new();
    private Dictionary<int, int> _dictionary;
    private Local _emuLocal;
    private EmbeddedResource _encryptedResource;
    private List<Instruction> _instructions;

    private List<Local> _locals;
    private MethodDef _method;

    public void Execute()
    {
        FindDelegateCreator();
        if (_delegateCreatorMethods.Count < 1)
        {
            Logger.Warn("Couldn't find any hidden call.");
            return;
        }

        _encryptedResource = FindMethodsDecrypterResource(_delegateCreatorMethods.First());
        _method = _delegateCreatorMethods.First();
        if (_method == null || _method.Body.Variables == null || _method.Body.Variables.Count < 1)
        {
            Logger.Warn("Couldn't find any hidden call.");
            return;
        }

        _locals = new List<Local>(_method.Body.Variables);
        var origInstrs = _method.Body.Instructions;
        if (!Find(_method.Body.Instructions, out var startIndex, out var endIndex, out _emuLocal))
            if (!FindStartEnd(origInstrs, out startIndex, out endIndex, out _emuLocal))
            {
                Logger.Warn("Couldn't find any hidden call.");
                return;
            }

        var num = endIndex - startIndex + 1;
        _instructions = new List<Instruction>(num);
        for (var i = 0; i < num; i++) _instructions.Add(origInstrs[startIndex + i].Clone());
        GetDictionary();
        var count = 0L;
        foreach (var type in DeobfuscatorContext.Module.GetTypes())
        foreach (var method in (from x in type.Methods where x.HasBody && x.Body.HasInstructions select x)
                 .ToArray())
            for (var i = 0; i < method.Body.Instructions.Count; i++)
                try
                {
                    if (method.Body.Instructions[i].OpCode.Equals(OpCodes.Ldsfld) &&
                        method.Body.Instructions[i + 1].OpCode.Equals(OpCodes.Call))
                    {
                        var field = method.Body.Instructions[i].Operand as IField;
                        GetCallInfo(field, out var iMethod, out var opCpde);
                        if (iMethod != null)
                        {
                            iMethod = DeobfuscatorContext.Module.Import(iMethod);
                            if (iMethod != null)
                            {
                                method.Body.Instructions[i].OpCode = OpCodes.Nop;
                                method.Body.Instructions[i + 1] = Instruction.Create(opCpde, iMethod);
                                method.Body.UpdateInstructionOffsets();
                                count += 1L;
                                Cleaner.TypesToRemove.Add(field.DeclaringType);
                            }
                        }
                    }
                } catch { }

        Cleaner.MethodsToRemove.Add(_method);
        if (count > 0L) Logger.Done((int) count + " Hidden calls restored.");
        else Logger.Warn("Couldn't find any hidden call.");
    }

    private bool Find(IList<Instruction> instrs, out int startIndex, out int endIndex, out Local tmpLocal)
    {
        startIndex = 0;
        endIndex = 0;
        tmpLocal = null;
        if (!FindStart(instrs, out var emuStartIndex, out _emuLocal)) return false;
        if (!FindEnd(instrs, emuStartIndex, out var emuEndIndex)) return false;
        startIndex = emuStartIndex;
        endIndex = emuEndIndex;
        tmpLocal = _emuLocal;
        return true;
    }

    private bool FindStartEnd(IList<Instruction> instrs, out int startIndex, out int endIndex, out Local tmpLocal)
    {
        var i = 0;
        while (i + 8 < instrs.Count)
        {
            if (instrs[i].OpCode.Code == Code.Conv_R_Un)
                if (instrs[i + 1].OpCode.Code == Code.Conv_R8)
                    if (instrs[i + 2].OpCode.Code == Code.Conv_U4)
                        if (instrs[i + 3].OpCode.Code == Code.Add)
                        {
                            var newEndIndex = i + 3;
                            var newStartIndex = -1;
                            for (var x = newEndIndex; x > 0; x--)
                                if (instrs[x].OpCode.FlowControl != FlowControl.Next)
                                {
                                    newStartIndex = x + 1;
                                    break;
                                }

                            if (newStartIndex > 0)
                            {
                                var checkLocs = new List<Local>();
                                var ckStartIndex = -1;
                                for (var y = newEndIndex; y >= newStartIndex; y--)
                                    if (CheckLocal(instrs[y], true) is { } loc)
                                    {
                                        if (!checkLocs.Contains(loc)) checkLocs.Add(loc);
                                        if (checkLocs.Count == 3) break;
                                        ckStartIndex = y;
                                    }

                                endIndex = newEndIndex;
                                startIndex = Math.Max(ckStartIndex, newStartIndex);
                                tmpLocal = CheckLocal(instrs[startIndex], true);
                                return true;
                            }
                        }

            i++;
        }

        endIndex = 0;
        startIndex = 0;
        tmpLocal = null;
        return false;
    }

    private bool FindStart(IList<Instruction> instrs, out int startIndex, out Local tmpLocal)
    {
        var i = 0;
        while (i + 8 < instrs.Count)
        {
            if (instrs[i].OpCode.Code == Code.Conv_U)
                if (instrs[i + 1].OpCode.Code == Code.Ldelem_U1)
                    if (instrs[i + 2].OpCode.Code == Code.Or)
                        if (CheckLocal(instrs[i + 3], false) != null)
                        {
                            Local local;
                            if ((local = CheckLocal(instrs[i + 4], true)) != null)
                                if (CheckLocal(instrs[i + 5], true) != null)
                                    if (instrs[i + 6].OpCode.Code == Code.Add)
                                        if (CheckLocal(instrs[i + 7], false) == local)
                                        {
                                            var instr = instrs[i + 8];
                                            var newStartIndex = i + 8;
                                            if (instr.IsBr())
                                            {
                                                instr = instr.Operand as Instruction;
                                                newStartIndex = instrs.IndexOf(instr);
                                            }

                                            if (newStartIndex > 0 && instr != null)
                                                if (CheckLocal(instr, true) == local)
                                                {
                                                    startIndex = newStartIndex;
                                                    tmpLocal = local;
                                                    return true;
                                                }
                                        }
                        }

            i++;
        }

        startIndex = 0;
        tmpLocal = null;
        return false;
    }

    private Local CheckLocal(Instruction instr, bool isLdloc)
    {
        if (isLdloc && !instr.IsLdloc()) return null;
        if (!isLdloc && !instr.IsStloc()) return null;
        return instr.GetLocal(_locals);
    }

    private bool FindEnd(IList<Instruction> instrs, int startIndex, out int endIndex)
    {
        for (var i = startIndex; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if (instr.OpCode.FlowControl != FlowControl.Next) break;
            if (instr.IsStloc() && instr.GetLocal(_locals) == _emuLocal)
            {
                endIndex = i - 1;
                return true;
            }
        }

        endIndex = 0;
        return false;
    }

    private EmbeddedResource FindMethodsDecrypterResource(MethodDef method)
    {
        foreach (var s in DotNetUtils.GetCodeStrings(method))
            if (DotNetUtils.GetResource(DeobfuscatorContext.Module, s) is EmbeddedResource resource)
                return resource;
        return null;
    }

    private void GetCallInfo(IField field, out IMethod calledMethod, out OpCode callOpcode)
    {
        callOpcode = OpCodes.Call;
        _dictionary.TryGetValue((int) field.MDToken.Raw, out var token);
        if ((token & 1073741824) > 0) callOpcode = OpCodes.Callvirt;
        token &= 1073741823;
        calledMethod = DeobfuscatorContext.Module.ResolveToken(token) as IMethod;
    }

    private void GetDictionary()
    {
        var resource = Decrypt();
        var length = resource.Length / 8;
        _dictionary = new Dictionary<int, int>();
        var reader = new BinaryReader(new MemoryStream(resource));
        for (var i = 0; i < length; i++)
        {
            var key = reader.ReadInt32();
            var value = reader.ReadInt32();
            if (!_dictionary.ContainsKey(key)) _dictionary.Add(key, value);
        }

        reader.Close();
    }

    private void FindDelegateCreator()
    {
        var callCounter = new CallCounter();
        foreach (var type in from x in DeobfuscatorContext.Module.GetTypes()
                 where x.Namespace.Equals("") && DotNetUtils.DerivesFromDelegate(x)
                 select x)
            if (type.FindStaticConstructor() is { } cctor)
                foreach (var method in DotNetUtils.GetMethodCalls(cctor))
                    if (method.MethodSig.GetParamCount() == 1 &&
                        method.GetParam(0).FullName == "System.RuntimeTypeHandle")
                        callCounter.Add(method);

        if (callCounter.Most() is { } mostCalls)
            _delegateCreatorMethods.Add(DotNetUtils.GetMethod(DeobfuscatorContext.Module, mostCalls));
    }

    private byte[] Decrypt()
    {
        var encrypted = _encryptedResource.CreateReader().ToArray();
        var decrypted = new byte[encrypted.Length];
        var sum = 0U;
        for (var i = 0; i < encrypted.Length; i += 4)
        {
            sum = CalculateMagic(sum);
            WriteUInt32(decrypted, i, sum ^ ReadUInt32(encrypted, i));
        }

        Cleaner.ResourceToRemove.Add(_encryptedResource);
        return decrypted;
    }

    private uint ReadUInt32(byte[] ary, int index)
    {
        var sizeLeft = ary.Length - index;
        if (sizeLeft >= 4) return BitConverter.ToUInt32(ary, index);
        return sizeLeft switch
        {
            1 => ary[index],
            2 => (uint) (ary[index] | (ary[index + 1] << 8)),
            3 => (uint) (ary[index] | (ary[index + 1] << 8) | (ary[index + 2] << 16)),
            _ => throw new ApplicationException("Can't read data")
        };
    }

    private void WriteUInt32(byte[] ary, int index, uint value)
    {
        var num = ary.Length - index;
        if (num >= 1) ary[index] = (byte) value;
        if (num >= 2) ary[index + 1] = (byte) (value >> 8);
        if (num >= 3) ary[index + 2] = (byte) (value >> 16);
        if (num >= 4) ary[index + 3] = (byte) (value >> 24);
    }

    private uint CalculateMagic(uint input)
    {
        _instrEmulator.Initialize(_method, _method.Parameters, _locals, _method.Body.InitLocals, false);
        _instrEmulator.SetLocal(_emuLocal, new Int32Value((int) input));
        foreach (var instr in _instructions) _instrEmulator.Emulate(instr);
        if (!(_instrEmulator.Pop() is Int32Value tos) || !tos.AllBitsValid())
            throw new Exception("Couldn't calculate magic value");
        return (uint) tos.Value;
    }
}