namespace DownloaderApp.Models
{
    /// <summary>
    /// Перечисление возможных состояний Circuit Breaker
    /// </summary>
    public enum CircuitBreakerState 
    { 
        /// <summary>
        /// Замкнут - нормальная работа
        /// </summary>
        Closed, 
        
        /// <summary>
        /// Разомкнут - операции не выполняются
        /// </summary>
        Open, 
        
        /// <summary>
        /// Полуоткрыт - проверка возможности возврата к нормальной работе
        /// </summary>
        HalfOpen 
    }
} 