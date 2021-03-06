﻿using Stackray.Collections;
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Jobs;

namespace Stackray.Entities {

  [BurstCompile]
  struct ChangedComponentToEntity<T> : IJobChunk where T : struct, IComponentData {
    [ReadOnly]
    public EntityTypeHandle EntityType;
    [ReadOnly]
    public ComponentTypeHandle<T> ChunkType;
    [WriteOnly]
    public NativeHashMap<Entity, T>.ParallelWriter ChangedComponents;
    public uint LastSystemVersion;
    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      if (!chunk.DidChange(ChunkType, LastSystemVersion))
        return;
      var entities = chunk.GetNativeArray(EntityType);
      var components = chunk.GetNativeArray(ChunkType);
      for (var i = 0; i < chunk.Count; ++i)
        ChangedComponents.TryAdd(entities[i], components[i]);
    }
  }

  [BurstCompile]
  struct ChangedTransformsToEntity : IJobParallelForTransform {
    [ReadOnly]
    public ComponentDataFromEntity<LocalToWorld> LocalToWorldFromEntity;
    [ReadOnly]
    [DeallocateOnJobCompletion]
    public NativeArray<Entity> Entities;
    [WriteOnly]
    public NativeHashMap<Entity, LocalToWorld>.ParallelWriter ChangedComponents;

    public void Execute(int index, TransformAccess transform) {
      var entity = Entities[index];
      if (LocalToWorldFromEntity.HasComponent(entity)) {
        var localToWorld = float4x4.TRS(transform.position, transform.rotation, transform.localScale);
        if (!LocalToWorldFromEntity[entity].Value.Equals(localToWorld))
          ChangedComponents.TryAdd(entity, new LocalToWorld { Value = localToWorld });
      }
    }
  }

  [BurstCompile]
  struct CopyFromChangedComponentData<T> : IJobChunk where T : struct, IComponentData {
    [ReadOnly]
    public EntityTypeHandle EntityType;
    public ComponentTypeHandle<T> ChunkType;
    [ReadOnly]
    public NativeHashMap<Entity, T> ChangedComponentData;

    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var entities = chunk.GetNativeArray(EntityType);
      var components = new NativeArray<T>();
      for(var i = 0; i < chunk.Count; ++i) {
        var entity = entities[i];
        if (ChangedComponentData.ContainsKey(entity)) {
          components = components.IsCreated ? components : chunk.GetNativeArray(ChunkType);
          components[i] = ChangedComponentData[entity];
        }
      }
    }
  }

  [BurstCompile]
  struct DidChange<T> : IJobChunk where T : struct, IComponentData {
    [ReadOnly]
    public ComponentTypeHandle<T> ChunkType;
    [NativeDisableContainerSafetyRestriction]
    public NativeUnit<bool> Changed;
    public uint LastSystemVersion;
    public bool ForceChange;
    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      if (Changed.Value || (!ForceChange && !chunk.DidChange(ChunkType, LastSystemVersion)))
        return;
      Changed.Value = true;
    }
  }

  [BurstCompile]
  struct GatherChunkChanged<T> : IJobChunk where T : struct, IComponentData {
    [ReadOnly]
    public ComponentTypeHandle<T> ChunkType;
    [WriteOnly]
    public NativeArray<int> ChangedIndices;
    public uint LastSystemVersion;
    public bool ForceChange;
    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      if (!ForceChange && !chunk.DidChange(ChunkType, LastSystemVersion))
        return;
      ChangedIndices[chunkIndex] = firstEntityIndex;
    }
  }

  [BurstCompile]
  struct ExtractChangedSlicesFromChunks : IJobParallelFor {
    [ReadOnly]
    public NativeArray<int> Source;
    [ReadOnly]
    [DeallocateOnJobCompletion]
    public NativeArray<ArchetypeChunk> Chunks;
    [WriteOnly]
    public NativeQueue<VTuple<int, int>>.ParallelWriter Slices;
    public void Execute(int index) {
      if (index > 0 && Source[index - 1] >= 0)
        return;
      var startEntityIndex = Source[index];
      var count = 0;
      var currIndex = index;
      while (currIndex < Source.Length && Source[currIndex] >= 0) {
        count += Chunks[currIndex].Count;
        currIndex++;
      }
      if (count > 0)
        Slices.Enqueue(new VTuple<int, int>(startEntityIndex, count));
    }
  }

  [BurstCompile]
  struct ConvertToDataWithIndex<T> : IJobParallelFor where T : struct, IComparable<T> {
    [ReadOnly]
    public NativeArray<T> Source;
    [WriteOnly]
    public NativeArray<DataWithIndex<T>> Target;
    public void Execute(int index) {
      Target[index] = new DataWithIndex<T> {
        Index = index,
        Value = Source[index]
      };
    }
  }

  [BurstCompile]
  struct ConvertToDataWithEntity<TData> : IJobChunk
    where TData : struct, IComparable<TData> {

    [ReadOnly]
    public EntityTypeHandle ChunkEntityType;
    [ReadOnly]
    public NativeArray<TData> Source;
    [WriteOnly]
    public NativeArray<DataWithEntity<TData>> Target;

    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var entities = chunk.GetNativeArray(ChunkEntityType);
      for(var i=0;i< chunk.Count; ++i) {
        var index = firstEntityIndex + i;
        Target[index] = new DataWithEntity<TData> {
          Entity = entities[i],
          Index = index,
          Value = Source[index]
        };
      }
    }
  }

  [BurstCompile]
  public struct GatherSharedComponentIndices<T> : IJobChunk where T : struct, ISharedComponentData {
    [ReadOnly]
    public SharedComponentTypeHandle<T> ChunkSharedComponentType;
    [WriteOnly]
    public NativeArray<int> Indices;
    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var sharedComponentIndex = chunk.GetSharedComponentIndex(ChunkSharedComponentType);
      for (var i = 0; i < chunk.Count; ++i)
        Indices[firstEntityIndex + i] = sharedComponentIndex;
    }
  }

  [BurstCompile]
  struct GatherEntityIndexMap : IJobChunk {
    [ReadOnly]
    public EntityTypeHandle EntityType;
    [WriteOnly]
    public NativeHashMap<Entity, int>.ParallelWriter EntityIndexMap;

    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var entities = chunk.GetNativeArray(EntityType);
      for (var i = 0; i < entities.Length; ++i)
        EntityIndexMap.TryAdd(entities[i], firstEntityIndex + i);
    }
  }

  [BurstCompile]
  struct GatherEntityComponentMap<T> : IJobChunk where T : struct, IComponentData {
    [ReadOnly]
    public EntityTypeHandle ChunkEntityType;
    [ReadOnly]
    public ComponentTypeHandle<T> ChunkDataType;
    [WriteOnly]
    public NativeHashMap<Entity, T>.ParallelWriter Result;

    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var entities = chunk.GetNativeArray(ChunkEntityType);
      var data = chunk.GetNativeArray(ChunkDataType);
      for (var i = 0; i < chunk.Count; ++i)
        Result.TryAdd(entities[i], data[i]);
    }
  }

  [BurstCompile]
  struct DestroyEntitiesOnly : IJobChunk {
    public EntityArchetype EntityOnlyArchetype;
    [ReadOnly]
    public EntityTypeHandle EntityType;
    [WriteOnly]
    public EntityCommandBuffer.ParallelWriter CmdBuffer;
    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var entities = chunk.GetNativeArray(EntityType);
      if (chunk.Archetype == EntityOnlyArchetype)
        for (var i = 0; i < chunk.Count; i++)
          CmdBuffer.DestroyEntity(firstEntityIndex + i, entities[i]);
    }
  }

  [BurstCompile]
  struct ResizeBufferDeferred<T> : IJobChunk where T : struct, IBufferElementData {
    public BufferTypeHandle<T> BufferType;
    [ReadOnly]
    public NativeCounter Length;

    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var bufferAccessor = chunk.GetBufferAccessor(BufferType);
      for (var i = 0; i < bufferAccessor.Length; ++i)
        bufferAccessor[i].ResizeUninitialized(Length.Value);
    }
  }

  [BurstCompile]
  struct ResizeBuffer<T> : IJobChunk where T : struct, IBufferElementData {
    public BufferTypeHandle<T> BufferType;
    public int Length;

    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var bufferAccessor = chunk.GetBufferAccessor(BufferType);
      for (var i = 0; i < bufferAccessor.Length; ++i)
        bufferAccessor[i].ResizeUninitialized(Length);
    }
  }

  [BurstCompile]
  struct CountBufferElements<T> : IJobChunk where T : struct, IBufferElementData {
    [ReadOnly]
    public BufferTypeHandle<T> ChunkBufferType;
    [WriteOnly]
    public NativeCounter.Concurrent Counter;

    public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex) {
      var buffer = chunk.GetBufferAccessor(ChunkBufferType);
      Counter.Increment(buffer.Length);
    }
  }
}
