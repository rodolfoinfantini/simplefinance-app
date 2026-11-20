import type { ApexOptions } from 'apexcharts'

import ThemedApexChart from './ThemedApexChart'
import { useChartTheme } from './useChartTheme'

const monthYearFormatter = new Intl.DateTimeFormat('pt-BR', {
  month: 'short',
  year: '2-digit',
})

const compactCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
  maximumFractionDigits: 0,
})

const tooltipCurrencyFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
})

export interface InstallmentMonthProjection {
  year: number
  month: number
  amount: number
  count: number
}

export interface InstallmentProjectionChartProps {
  items: InstallmentMonthProjection[]
  seriesLabel?: string
  installmentsWord?: string
  height?: number
}

export function InstallmentProjectionChart({
  items,
  seriesLabel = 'Parcelas',
  installmentsWord = 'parcelas',
  height = 280,
}: InstallmentProjectionChartProps) {
  const theme = useChartTheme()

  const getOptions = (): ApexOptions => ({
    chart: {
      type: 'bar',
      foreColor: theme.text,
      toolbar: { show: false },
    },
    plotOptions: {
      bar: {
        columnWidth: '58%',
        borderRadius: 4,
        dataLabels: { position: 'top' },
      },
    },
    colors: [theme.primary],
    dataLabels: {
      enabled: true,
      offsetY: -20,
      formatter: (val) => compactCurrencyFormatter.format(Number(val)),
      style: {
        fontSize: '11px',
        colors: [theme.text],
      },
    },
    xaxis: {
      categories: items.map((item) => monthYearFormatter.format(new Date(item.year, item.month - 1, 1))),
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
          const countLabel = item ? ` (${item.count} ${installmentsWord})` : ''
          return `${tooltipCurrencyFormatter.format(Number(val))}${countLabel}`
        },
      },
    },
  })

  return (
    <ThemedApexChart
      type="bar"
      height={height}
      series={[{ name: seriesLabel, data: items.map((item) => item.amount) }]}
      getOptions={getOptions}
    />
  )
}
