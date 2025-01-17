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

using NETReactorSlayer.De4dot;
using NETReactorSlayer.De4dot.Renamer;

namespace NETReactorSlayer.Core.Deobfuscators
{
    internal class SymbolRenamer : IStage
    {
        public void Execute()
        {
            Logger.Done("Renaming obfuscated symbols...");
            var deobfuscator =
                new DeobfuscatorInfo(Context.Module, Context.Options.RenameShort).CreateDeobfuscator();
            var obfuscatedFile = new ObfuscatedFile(Context.Module, deobfuscator);
            obfuscatedFile.DeobfuscatorOptions.RenamerFlags = Context.Options.RenamerFlags;
            new Renamer(obfuscatedFile).Rename();
        }
    }
}