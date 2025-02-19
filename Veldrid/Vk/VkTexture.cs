﻿using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using static Veldrid.Vk.VulkanUtil;
using System.Diagnostics;
using System;

namespace Veldrid.Vk
{
    internal unsafe class VkTexture : Texture
    {
        private readonly VkGraphicsDevice _gd;
        private readonly VkImage _optimalImage;
        private readonly VkMemoryBlock _memoryBlock;
        private readonly Vortice.Vulkan.VkBuffer _stagingBuffer;
        private PixelFormat _format; // Static for regular images -- may change for shared staging images
        private readonly uint _actualImageArrayLayers;
        private bool _destroyed;

        // Immutable except for shared staging Textures.
        private uint _width;
        private uint _height;
        private uint _depth;

        public override uint Width => _width;

        public override uint Height => _height;

        public override uint Depth => _depth;

        public override PixelFormat Format => _format;

        public override uint MipLevels { get; }

        public override uint ArrayLayers { get; }

        public override TextureUsage Usage { get; }

        public override TextureType Type { get; }

        public override TextureSampleCount SampleCount { get; }

        public VkImage OptimalDeviceImage => _optimalImage;
        public Vortice.Vulkan.VkBuffer StagingBuffer => _stagingBuffer;
        public VkMemoryBlock Memory => _memoryBlock;

        public VkFormat VkFormat { get; }
        public VkSampleCountFlags VkSampleCount { get; }

        private VkImageLayout[] _imageLayouts;
        private string _name;

        public ResourceRefCount RefCount { get; }

        internal VkTexture(VkGraphicsDevice gd, ref TextureDescription description)
        {
            _gd = gd;
            _width = description.Width;
            _height = description.Height;
            _depth = description.Depth;
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            bool isCubemap = ((description.Usage) & TextureUsage.Cubemap) == TextureUsage.Cubemap;
            _actualImageArrayLayers = isCubemap
                ? 6 * ArrayLayers
                : ArrayLayers;
            _format = description.Format;
            Usage = description.Usage;
            Type = description.Type;
            SampleCount = description.SampleCount;
            VkSampleCount = VkFormats.VdToVkSampleCount(SampleCount);
            VkFormat = VkFormats.VdToVkPixelFormat(Format, (description.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil);

            bool isStaging = (Usage & TextureUsage.Staging) == TextureUsage.Staging;

            if (!isStaging)
            {
                var imageCI = new VkImageCreateInfo
                {
                    sType = VkStructureType.ImageCreateInfo,
                    mipLevels = MipLevels,
                    arrayLayers = _actualImageArrayLayers,
                    imageType = VkFormats.VdToVkTextureType(Type),
                    extent = new VkExtent3D
                    {
                        width = Width,
                        height = Height,
                        depth = Depth
                    },
                    initialLayout = VkImageLayout.Preinitialized,
                    usage = VkFormats.VdToVkTextureUsage(Usage),
                    tiling = isStaging ? VkImageTiling.Linear : VkImageTiling.Optimal,
                    format = VkFormat,
                    flags = VkImageCreateFlags.MutableFormat,
                    samples = VkSampleCount
                };

                if (isCubemap)
                {
                    imageCI.flags |= VkImageCreateFlags.CubeCompatible;
                }

                uint subresourceCount = MipLevels * _actualImageArrayLayers * Depth;
                VkResult result = vkCreateImage(gd.Device, &imageCI, null, out _optimalImage);
                CheckResult(result);

                VkMemoryRequirements memoryRequirements;
                bool prefersDedicatedAllocation;
                if (_gd.GetImageMemoryRequirements2 != null)
                {
                    var memReqsInfo2 = new VkImageMemoryRequirementsInfo2
                    {
                        sType = VkStructureType.ImageMemoryRequirementsInfo2,
                        image = _optimalImage
                    };
                    var dedicatedReqs = new VkMemoryDedicatedRequirements
                    {
                        sType = VkStructureType.MemoryDedicatedRequirements
                    };
                    var memReqs2 = new VkMemoryRequirements2
                    {
                        sType = VkStructureType.MemoryRequirements2,
                        pNext = &dedicatedReqs
                    };
                    _gd.GetImageMemoryRequirements2(_gd.Device, &memReqsInfo2, &memReqs2);
                    memoryRequirements = memReqs2.memoryRequirements;
                    prefersDedicatedAllocation = dedicatedReqs.prefersDedicatedAllocation || dedicatedReqs.requiresDedicatedAllocation;
                }
                else
                {
                    vkGetImageMemoryRequirements(gd.Device, _optimalImage, out memoryRequirements);
                    prefersDedicatedAllocation = false;
                }

                VkMemoryBlock memoryToken = gd.MemoryManager.Allocate(
                    gd.PhysicalDeviceMemProperties,
                    memoryRequirements.memoryTypeBits,
                    VkMemoryPropertyFlags.DeviceLocal,
                    false,
                    memoryRequirements.size,
                    memoryRequirements.alignment,
                    prefersDedicatedAllocation,
                    _optimalImage,
                    Vortice.Vulkan.VkBuffer.Null);
                _memoryBlock = memoryToken;
                result = vkBindImageMemory(gd.Device, _optimalImage, _memoryBlock.DeviceMemory, _memoryBlock.Offset);
                CheckResult(result);

                _imageLayouts = new VkImageLayout[subresourceCount];
                for (int i = 0; i < _imageLayouts.Length; i++)
                {
                    _imageLayouts[i] = VkImageLayout.Preinitialized;
                }
            }
            else // isStaging
            {
                uint depthPitch = FormatHelpers.GetDepthPitch(
                    FormatHelpers.GetRowPitch(Width, Format),
                    Height,
                    Format);
                uint stagingSize = depthPitch * Depth;
                for (uint level = 1; level < MipLevels; level++)
                {
                    Util.GetMipDimensions(this, level, out uint mipWidth, out uint mipHeight, out uint mipDepth);

                    depthPitch = FormatHelpers.GetDepthPitch(
                        FormatHelpers.GetRowPitch(mipWidth, Format),
                        mipHeight,
                        Format);

                    stagingSize += depthPitch * mipDepth;
                }
                stagingSize *= ArrayLayers;

                var bufferCI = new VkBufferCreateInfo
                {
                    sType = VkStructureType.BufferCreateInfo,
                    usage = VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst,
                    size = stagingSize
                };
                VkResult result = vkCreateBuffer(_gd.Device, &bufferCI, null, out _stagingBuffer);
                CheckResult(result);

                VkMemoryRequirements bufferMemReqs;
                bool prefersDedicatedAllocation;
                if (_gd.GetBufferMemoryRequirements2 != null)
                {
                    var memReqInfo2 = new VkBufferMemoryRequirementsInfo2
                    {
                        sType = VkStructureType.BufferMemoryRequirementsInfo2,
                        buffer = _stagingBuffer
                    };
                    var dedicatedReqs = new VkMemoryDedicatedRequirements
                    {
                        sType = VkStructureType.MemoryDedicatedRequirements
                    };
                    var memReqs2 = new VkMemoryRequirements2
                    {
                        sType = VkStructureType.MemoryRequirements2,
                        pNext = &dedicatedReqs
                    };
                    _gd.GetBufferMemoryRequirements2(_gd.Device, &memReqInfo2, &memReqs2);
                    bufferMemReqs = memReqs2.memoryRequirements;
                    prefersDedicatedAllocation = dedicatedReqs.prefersDedicatedAllocation || dedicatedReqs.requiresDedicatedAllocation;
                }
                else
                {
                    vkGetBufferMemoryRequirements(gd.Device, _stagingBuffer, out bufferMemReqs);
                    prefersDedicatedAllocation = false;
                }

                _memoryBlock = _gd.MemoryManager.Allocate(
                    _gd.PhysicalDeviceMemProperties,
                    bufferMemReqs.memoryTypeBits,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent,
                    true,
                    bufferMemReqs.size,
                    bufferMemReqs.alignment,
                    prefersDedicatedAllocation,
                    VkImage.Null,
                    _stagingBuffer);

                result = vkBindBufferMemory(_gd.Device, _stagingBuffer, _memoryBlock.DeviceMemory, _memoryBlock.Offset);
                CheckResult(result);
            }

            ClearIfRenderTarget();
            TransitionIfSampled();
            RefCount = new ResourceRefCount(RefCountedDispose);
        }

        // Used to construct Swapchain textures.
        internal VkTexture(
            VkGraphicsDevice gd,
            uint width,
            uint height,
            uint mipLevels,
            uint arrayLayers,
            VkFormat vkFormat,
            TextureUsage usage,
            TextureSampleCount sampleCount,
            VkImage existingImage)
        {
            Debug.Assert(width > 0 && height > 0);
            _gd = gd;
            MipLevels = mipLevels;
            _width = width;
            _height = height;
            _depth = 1;
            VkFormat = vkFormat;
            _format = VkFormats.VkToVdPixelFormat(VkFormat);
            ArrayLayers = arrayLayers;
            Usage = usage;
            Type = TextureType.Texture2D;
            SampleCount = sampleCount;
            VkSampleCount = VkFormats.VdToVkSampleCount(sampleCount);
            _optimalImage = existingImage;
            _imageLayouts = new[] { VkImageLayout.Undefined };

            ClearIfRenderTarget();
            RefCount = new ResourceRefCount(DisposeCore);
        }

        private void ClearIfRenderTarget()
        {
            // If the image is going to be used as a render target, we need to clear the data before its first use.
            if ((Usage & TextureUsage.RenderTarget) != 0)
            {
                _gd.ClearColorTexture(this, new VkClearColorValue(0, 0, 0, 0));
            }
            else if ((Usage & TextureUsage.DepthStencil) != 0)
            {
                _gd.ClearDepthTexture(this, new VkClearDepthStencilValue(0, 0));
            }
        }

        private void TransitionIfSampled()
        {
            if ((Usage & TextureUsage.Sampled) != 0)
            {
                _gd.TransitionImageLayout(this, VkImageLayout.ShaderReadOnlyOptimal);
            }
        }

        internal VkSubresourceLayout GetSubresourceLayout(uint subresource)
        {
            bool staging = _stagingBuffer.Handle != 0;
            Util.GetMipLevelAndArrayLayer(this, subresource, out uint mipLevel, out uint arrayLayer);
            if (!staging)
            {
                VkImageAspectFlags aspect = (Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil
                  ? (VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil)
                  : VkImageAspectFlags.Color;
                VkImageSubresource imageSubresource = new VkImageSubresource
                {
                    arrayLayer = arrayLayer,
                    mipLevel = mipLevel,
                    aspectMask = aspect,
                };

                vkGetImageSubresourceLayout(_gd.Device, _optimalImage, &imageSubresource, out VkSubresourceLayout layout);
                return layout;
            }
            else
            {
                uint blockSize = FormatHelpers.IsCompressedFormat(Format) ? 4u : 1u;
                Util.GetMipDimensions(this, mipLevel, out uint mipWidth, out uint mipHeight, out uint mipDepth);
                uint rowPitch = FormatHelpers.GetRowPitch(mipWidth, Format);
                uint depthPitch = FormatHelpers.GetDepthPitch(rowPitch, mipHeight, Format);

                VkSubresourceLayout layout = new VkSubresourceLayout()
                {
                    rowPitch = rowPitch,
                    depthPitch = depthPitch,
                    arrayPitch = depthPitch,
                    size = depthPitch,
                };
                layout.offset = Util.ComputeSubresourceOffset(this, mipLevel, arrayLayer);

                return layout;
            }
        }

        internal void TransitionImageLayout(
            VkCommandBuffer cb,
            uint baseMipLevel,
            uint levelCount,
            uint baseArrayLayer,
            uint layerCount,
            VkImageLayout newLayout)
        {
            if (_stagingBuffer != Vortice.Vulkan.VkBuffer.Null)
            {
                return;
            }

            VkImageLayout oldLayout = _imageLayouts[CalculateSubresource(baseMipLevel, baseArrayLayer)];
#if DEBUG
            for (uint level = 0; level < levelCount; level++)
            {
                for (uint layer = 0; layer < layerCount; layer++)
                {
                    if (_imageLayouts[CalculateSubresource(baseMipLevel + level, baseArrayLayer + layer)] != oldLayout)
                    {
                        throw new VeldridException("Unexpected image layout.");
                    }
                }
            }
#endif
            if (oldLayout != newLayout)
            {
                VkImageAspectFlags aspectMask;
                if ((Usage & TextureUsage.DepthStencil) != 0)
                {
                    aspectMask = FormatHelpers.IsStencilFormat(Format)
                        ? aspectMask = VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil
                        : aspectMask = VkImageAspectFlags.Depth;
                }
                else
                {
                    aspectMask = VkImageAspectFlags.Color;
                }
                VulkanUtil.TransitionImageLayout(
                    cb,
                    OptimalDeviceImage,
                    baseMipLevel,
                    levelCount,
                    baseArrayLayer,
                    ((Usage & TextureUsage.Cubemap) > 0) ? _actualImageArrayLayers : layerCount,//layerCount,
                    aspectMask,
                    _imageLayouts[CalculateSubresource(baseMipLevel, baseArrayLayer)],
                    newLayout);

                for (uint level = 0; level < levelCount; level++)
                {
                    for (uint layer = 0; layer < (((Usage & TextureUsage.Cubemap) > 0) ? _actualImageArrayLayers : layerCount); layer++)
                    {
                        _imageLayouts[CalculateSubresource(baseMipLevel + level, baseArrayLayer + layer)] = newLayout;
                    }
                }
            }
        }

        internal VkImageLayout GetImageLayout(uint mipLevel, uint arrayLayer)
        {
            return _imageLayouts[CalculateSubresource(mipLevel, arrayLayer)];
        }

        public override string Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetResourceName(this, value);
            }
        }

        internal void SetStagingDimensions(uint width, uint height, uint depth, PixelFormat format)
        {
            Debug.Assert(_stagingBuffer != Vortice.Vulkan.VkBuffer.Null);
            Debug.Assert(Usage == TextureUsage.Staging);
            _width = width;
            _height = height;
            _depth = depth;
            _format = format;
        }

        private protected override void DisposeCore()
        {
            RefCount.Decrement();
        }

        private void RefCountedDispose()
        {
            if (!_destroyed)
            {
                base.Dispose();

                _destroyed = true;

                bool isStaging = (Usage & TextureUsage.Staging) == TextureUsage.Staging;
                if (isStaging)
                {
                    vkDestroyBuffer(_gd.Device, _stagingBuffer, null);
                }
                else
                {
                    vkDestroyImage(_gd.Device, _optimalImage, null);
                }

                if (_memoryBlock.DeviceMemory.Handle != 0)
                {
                    _gd.MemoryManager.Free(_memoryBlock);
                }
            }
        }

        internal void SetImageLayout(uint mipLevel, uint arrayLayer, VkImageLayout layout)
        {
            _imageLayouts[CalculateSubresource(mipLevel, arrayLayer)] = layout;
        }
    }
}
