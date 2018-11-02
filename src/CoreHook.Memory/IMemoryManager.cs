﻿using System;
using System.Collections.Generic;
using System.Text;

namespace CoreHook.Memory
{
    public interface IMemoryManager : IMemoryAllocator, IMemoryReader, IMemoryWriter, IDisposable
    {
    }
}