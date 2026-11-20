import type { ApexOptions } from 'apexcharts'

import ThemedApexChart from './ThemedApexChart'
import { useChartTheme } from './useChartTheme'

const compactCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
  maximumFractionDigits: 0,
})

const tooltipCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
})

export interface WeekdaySpendChartItem {
  dayOfWeek: number
  label: string
  totalAmount: number
  transactionCount: number
  averageAmount: number
}

export interface WeekdaySpendingChartProps {
  items: WeekdaySpendChartItem[]
  seriesLabel?: string
  transactionsWord?: string
  averageLabel?: string
  height?: number
}

export function WeekdaySpendingChart({
  items,
  seriesLabel = 'Gastos',
  transactionsWord = 'transações',
  averageLabel = 'Média',
  height = 300,
}: WeekdaySpendingChartProps) {
  const theme = useChartTheme()
  const maxAmount = Math.max(0, ...items.map((i) => i.totalAmount))
  const barColors = items.map((item) =>
    maxAmount > 0 && item.totalAmount === maxAmount ? theme.expense : theme.primary
  )

  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'bar',
      foreColor: theme.text,
      toolbar: { show: false },
    },
    plotOptions: {
      bar: {
        columnWidth: '55%',
        distributed: true,
        borderRadius: 4,
        dataLabels: { position: 'top' },
      },
    },
    colors: barColors,
    dataLabels: {
      enabled: true,
      offsetY: -20,
      formatter: (val) => (Number(val) > 0 ? compactCurrencyFormatter.format(Number(val)) : ''),
      style: {
        fontSize: '11px',
        colors: [theme.text],
      },
    },
    xaxis: {
      categories: items.map((item) => item.label),
      labels: { style: { colors: theme.text } },
      axisBorder: { color: theme.grid },
      axisTicks: { color: theme.grid },
    },
    yaxis: {
      labels: {
        style: { colors: theme.text },
        formatter: (val) => compactCurrencyFormatter.format(Number(val)),
      },
    },
    grid: { borderColor: theme.grid },
    legend: { show: false },
    tooltip: {
      theme: theme.mode,
      y: {
        formatter: (val, opts) => {
          const item = items[opts?.dataPointIndex ?? -1]
          if (!item) return tooltipCurrencyFormatter.format(Number(val))
          return `${tooltipCurrencyFormatter.format(item.totalAmount)} (${item.transactionCount} ${transactionsWord} · ${averageLabel}: ${tooltipCurrencyFormatter.format(item.averageAmount)})`
        },
      },
    },
  })

  return (
    <ThemedApexChart
      type="bar"
      height={height}
      series={[{ name: seriesLabel, data: items.map((item) => item.totalAmount) }]}
      getOptions={getOptions}
    />
  )
}
