﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RapidCMS.Common.Data;
using TestLibrary.Entities;

namespace TestLibrary.Repositories
{
    public class ValidationRepository : BaseStructRepository<int, int, ValidationEntity>
    {
        private readonly Dictionary<int, ValidationEntity> _data;

        public ValidationRepository()
        {
            _data = new Dictionary<int, ValidationEntity>
            {
                {
                    1,
                    new ValidationEntity {
                        Id = "1",
                        Name = "Name",
                        NotRequired = "fdsa",
                        Range = 3
                    }
                }
            };
        }

        public override Task DeleteAsync(int id, int? parentId)
        {
            _data.Remove(id);

            return Task.CompletedTask;
        }

        public override Task<IEnumerable<ValidationEntity>> GetAllAsync(int? parentId)
        {
            return Task.FromResult(_data.Select(x => x.Value));
        }

        public override Task<ValidationEntity> GetByIdAsync(int id, int? parentId)
        {
            return Task.FromResult(_data[id]);
        }

        public override Task<ValidationEntity> InsertAsync(int? parentId, ValidationEntity entity, IRelationContainer relations)
        {
            entity.Id = (_data.Count + 1).ToString();

            _data[_data.Count + 1] = entity;

            return Task.FromResult(_data.Last().Value);
        }

        public override Task<ValidationEntity> NewAsync(int? parentId, Type? variantType = null)
        {
            return Task.FromResult(new ValidationEntity());
        }

        public override int ParseKey(string id)
        {
            return int.Parse(id);
        }

        public override int? ParseParentKey(string? parentId)
        {
            return int.TryParse(parentId, out var id) ? id : default(int?);
        }

        public override Task UpdateAsync(int id, int? parentId, ValidationEntity entity, IRelationContainer relations)
        {
            _data[id] = entity;

            return Task.CompletedTask;
        }
    }
}