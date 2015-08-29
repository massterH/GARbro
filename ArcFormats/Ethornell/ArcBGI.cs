//! \file       ArcBGI.cs
//! \date       Tue Sep 09 09:29:12 2014
//! \brief      BGI/Ethornell engine archive implementation.
//
// Copyright (C) 2014-2015 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using GameRes.Utility;

namespace GameRes.Formats.BGI
{
    [Export(typeof(ArchiveFormat))]
    public class ArcOpener : ArchiveFormat
    {
        public override string         Tag { get { return "BGI"; } }
        public override string Description { get { return "BGI/Ethornell engine resource archive"; } }
        public override uint     Signature { get { return 0x6b636150; } } // "Pack"
        public override bool  IsHierarchic { get { return false; } }
        public override bool     CanCreate { get { return false; } }

        public ArcOpener ()
        {
            Extensions = new string[] { "arc" };
        }

        public override ArcFile TryOpen (ArcView file)
        {
            if (!file.View.AsciiEqual (4, "File    "))
                return null;
            uint count = file.View.ReadUInt32 (12);
            if (count > 0xfffff)
                return null;
            uint index_size = 0x20 * count;
            if (index_size > file.View.Reserve (0x10, index_size))
                return null;
            var dir = new List<Entry> ((int)count);
            long index_offset = 0x10;
            long base_offset = index_offset + index_size;
            for (uint i = 0; i < count; ++i)
            {
                string name = file.View.ReadString (index_offset, 0x10);
                var entry = FormatCatalog.Instance.CreateEntry (name);
                entry.Offset = base_offset + file.View.ReadUInt32 (index_offset+0x10);
                entry.Size   = file.View.ReadUInt32 (index_offset+0x14);
                if (!entry.CheckPlacement (file.MaxOffset))
                    return null;
                dir.Add (entry);
                index_offset += 0x20;
            }
            return new ArcFile (file, this, dir);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var entry_offset = entry.Offset;
            var input = new ArcView.Frame (arc.File, entry_offset, entry.Size);
            try
            {
                if (entry.Size > 0x220 && input.AsciiEqual (entry_offset, "DSC FORMAT 1.00\0"))
                {
                    using (var decoder = new DscDecoder (input))
                    {
                        decoder.Unpack();
                        byte[] data = decoder.Output;
                        if (data.Length > 0x40)
                        {
                            uint data_offset = LittleEndian.ToUInt32 (data, 0);
                            if (data_offset < data.Length-4 && 0x20207762 == LittleEndian.ToUInt32 (data, 4))
                            {
                                if (0x5367674f == LittleEndian.ToUInt32 (data, (int)data_offset)) // 'OggS'
                                {
                                    return new MemoryStream (data, (int)data_offset, data.Length-(int)data_offset, false);
                                }
                            }
                        }
                        return new MemoryStream (data, false);
                    }
                }
                if (entry.Size > 0x40)
                {
                    uint data_offset = input.ReadUInt32 (entry_offset);
                    if (data_offset < entry.Size-4 && 0x20207762 == input.ReadUInt32 (entry_offset+4))
                    {
                        if (0x5367674f == input.ReadUInt32 (entry_offset+data_offset)) // 'OggS'
                        {
                            return new ArcView.ArcStream (input, entry_offset+data_offset, entry.Size-data_offset);
                        }
                    }
                }
                return new ArcView.ArcStream (input);
            }
            catch (Exception X)
            {
                System.Diagnostics.Trace.WriteLine (X.Message, "BgiOpener");
                return arc.File.CreateStream (entry.Offset, entry.Size);
            }
        }
    }

    internal class BgiDecoderBase : MsbBitStream
    {
        protected uint      m_key;
        protected uint      m_magic;

        protected BgiDecoderBase (Stream input, bool leave_open = false) : base (input, leave_open)
        {
        }

        protected byte UpdateKey ()
        {
            uint v0 = 20021 * (m_key & 0xffff);
            uint v1 = m_magic | (m_key >> 16);
            v1 = v1 * 20021 + m_key * 346;
            v1 = (v1 + (v0 >> 16)) & 0xffff;
            m_key = (v1 << 16) + (v0 & 0xffff) + 1;
            return (byte)v1;
        }
    }

    internal sealed class DscDecoder : BgiDecoderBase
    {
        byte[]    m_output;
        uint      m_dst_size;
        uint      m_dec_count;

        public byte[] Output { get { return m_output; } }

        public DscDecoder (ArcView.Frame input)
            : base (new ArcView.ArcStream (input, input.Offset+0x20, input.Reserved-0x20))
        {
            m_magic = (uint)input.ReadUInt16 (input.Offset) << 16;
            m_key = input.ReadUInt32 (input.Offset+0x10);
            m_dst_size = input.ReadUInt32 (input.Offset+0x14);
            m_dec_count = input.ReadUInt32 (input.Offset+0x18);
            m_output = new byte[m_dst_size];
        }

        public void Unpack ()
        {
            HuffmanCode[] hcodes = new HuffmanCode[512];
            HuffmanNode[] hnodes = new HuffmanNode[1023];

            int leaf_node_count = 0;
            for (ushort i = 0; i < 512; i++)
            {
                int src = Input.ReadByte();
                if (-1 == src)
                    throw new EndOfStreamException ("Incomplete compressed stream");
                byte depth = (byte)(src - UpdateKey());
                if (0 != depth)
                {
                    hcodes[leaf_node_count].Depth = depth;
                    hcodes[leaf_node_count].Code = i;
                    leaf_node_count++;
                }
            }
            Array.Sort (hcodes, 0, leaf_node_count);
            CreateHuffmanTree (hnodes, hcodes, leaf_node_count);
            HuffmanDecompress (hnodes, m_dec_count);
        }

        struct HuffmanCode : IComparable<HuffmanCode>
        {
            public ushort Code;
            public ushort Depth;

            public int CompareTo (HuffmanCode other)
            {
                int cmp = (int)Depth - (int)other.Depth;
                if (0 == cmp)
                    cmp = (int)Code - (int)other.Code;
                return cmp;
            }
        }

        class HuffmanNode
        {
            public bool IsParent;
            public uint Code;
            public uint LeftChildIndex;
            public uint RightChildIndex;
        }

        static void CreateHuffmanTree (HuffmanNode[] hnodes, HuffmanCode[] hcode, int node_count)
        {
            uint[,] nodes_index = new uint[2,512];
            uint next_node_index = 1;
            int depth_nodes = 1;
            uint depth = 0;
            int switch_flag = 0;
            nodes_index[0,0] = 0;
            for (int n = 0; n < node_count; )
            {
                int huffman_nodes_index = switch_flag;
                switch_flag ^= 1;
                int child_index = switch_flag;

                int depth_existed_nodes = 0;
                while (hcode[n].Depth == depth)
                {
                    var node = new HuffmanNode { IsParent = false, Code = hcode[n++].Code };
                    hnodes[nodes_index[huffman_nodes_index, depth_existed_nodes]] = node;
                    depth_existed_nodes++;
                }
                int depth_nodes_to_create = depth_nodes - depth_existed_nodes;
                for (int i = 0; i < depth_nodes_to_create; i++)
                {
                    var node = new HuffmanNode { IsParent = true };
                    nodes_index[child_index, i * 2]     = node.LeftChildIndex = next_node_index++;
                    nodes_index[child_index, i * 2 + 1] = node.RightChildIndex = next_node_index++;
                    hnodes[nodes_index[huffman_nodes_index, depth_existed_nodes+i]] = node;
                }
                depth++;
                depth_nodes = depth_nodes_to_create * 2;
            }
        }

        uint HuffmanDecompress (HuffmanNode[] hnodes, uint dec_count)
        {
            uint dst_ptr = 0;

            for (uint k = 0; k < dec_count; k++)
            {
                uint node_index = 0;
                do
                {
                    int bit = GetNextBit();
                    if (-1 == bit)
                        throw new EndOfStreamException();
                    if (0 == bit)
                        node_index = hnodes[node_index].LeftChildIndex;
                    else
                        node_index = hnodes[node_index].RightChildIndex;
                }
                while (hnodes[node_index].IsParent);

                uint code = hnodes[node_index].Code;
                if (code >= 256)
                {
                    int win_pos = GetBits (12);
                    if (-1 == win_pos)
                        break;
            
                    uint copy_bytes = (code & 0xff) + 2;
                    win_pos += 2;			
                    for (uint i = 0; i < copy_bytes; i++)
                    {
                        m_output[dst_ptr] = m_output[dst_ptr - win_pos];
                        dst_ptr++;
                    }
                } else
                    m_output[dst_ptr++] = (byte)code;
            }
            return dst_ptr;
        }
    }
}