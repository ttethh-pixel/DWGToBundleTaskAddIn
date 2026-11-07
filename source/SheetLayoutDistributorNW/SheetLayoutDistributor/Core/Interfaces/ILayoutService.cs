using SheetLayoutDistributor.Core.Models;

namespace SheetLayoutDistributor.Core.Interfaces
{
    public interface ILayoutService
    {
        /// <summary>
        /// Основная точка входа для создания листов.
        /// </summary>
        void CreateLayoutsFromModel();
    }
}