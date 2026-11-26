using System;
using System.Collections.Generic;
using Scio.Abstractions;
using Xunit;

namespace Scio.Core.Tests
{
    public sealed class Ola4AuditInteropTests
    {
        [Fact]
        public void BuildBc3Document_ProducesValidFiebdcRecords()
        {
            var items = new List<BudgetLineItem>
            {
                new BudgetLineItem { Code = "01.01", Description = "Concreto f'c=210 kg/cm2", Unit = "m3", Quantity = 120.5, UnitPrice = 420.0 }
            };
            string bc3 = Bc3AndAuditKit.BuildBc3Document("OBRA01", "Torre Piloto", "PEN", items);
            Assert.Contains("~V|SCIO DC|FIEBDC-3/2020|", bc3);
            Assert.Contains("~C|01.01|m3|Concreto", bc3);
            Assert.Contains("~D|OBRA01##|01.01\\1\\120.500\\", bc3);
        }

        [Fact]
        public void CompareBudgets_ComputesDeltasAndVariance()
        {
            var b = new[] { new BudgetLineItem { Code = "A1", Description = "Acero", Unit = "kg", Quantity = 1000, UnitPrice = 5.0 } };
            var c = new[]
            {
                new BudgetLineItem { Code = "A1", Description = "Acero", Unit = "kg", Quantity = 1200, UnitPrice = 5.0 },
                new BudgetLineItem { Code = "B1", Description = "Encofrado", Unit = "m2", Quantity = 100, UnitPrice = 40.0 }
            };
            var diff = Bc3AndAuditKit.CompareBudgets(b, c);
            Assert.Equal(5000.0, diff.Value<double>("baseline_total"));
            Assert.Equal(10000.0, diff.Value<double>("current_total"));
            Assert.Equal(100.0, diff.Value<double>("variance_pct"));
            Assert.Equal(1, diff.Value<int>("added"));
            Assert.Equal(1, diff.Value<int>("changed"));
        }

        [Fact]
        public void Evaluate4DStatus_ClassifiesDatesCorrectly()
        {
            var s = new DateTime(2026, 5, 1);
            var e = new DateTime(2026, 5, 20);
            Assert.Equal("not_started", Bc3AndAuditKit.Evaluate4DStatus(new DateTime(2026, 4, 25), s, e));
            Assert.Equal("in_progress", Bc3AndAuditKit.Evaluate4DStatus(new DateTime(2026, 5, 10), s, e));
            Assert.Equal("completed", Bc3AndAuditKit.Evaluate4DStatus(new DateTime(2026, 5, 25), s, e));
        }
    }
}
