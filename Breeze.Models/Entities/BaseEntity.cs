﻿using Breeze.Models.Constants;
using Breeze.Utilities;
using System.ComponentModel.DataAnnotations.Schema;

namespace Breeze.Models.Entities;

public class BaseEntity
{
    [Column(DbColumnNames.CREATED_BY)]
    public string CreatedBy { get; set; } = string.Empty;

    [Column(DbColumnNames.CREATED_DATE)]
    public DateTime CreatedDate { get; set; }

    [Column(DbColumnNames.MODIFIED_BY)]
    public string? ModifiedBy { get; set; }

    [Column(DbColumnNames.MODIFIED_DATE)]
    public DateTime? ModifiedDate { get; set; }

    [Column(DbColumnNames.DELETED)]
    public bool Deleted { get; set; } = false;

    [Column(DbColumnNames.ROW_VERSION)]
    public byte[] RowVersion { get; set; } = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
}