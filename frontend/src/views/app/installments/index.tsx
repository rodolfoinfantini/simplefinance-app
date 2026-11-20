import { useTranslation } from 'react-i18next'

import { PageHeader } from '@/components/PageHeader'
import { InstallmentsPage } from '@/features/installments/InstallmentsPage'

const Page = () => {
  const { t } = useTranslation('menu')

  return (
    <>
      <PageHeader title={t('items.installments')} />
      <InstallmentsPage />
    </>
  )
}

export default Page
