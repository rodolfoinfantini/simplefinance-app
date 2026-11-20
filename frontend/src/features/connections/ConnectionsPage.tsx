import type { TFunction } from 'i18next'
import { useState } from 'react'
import { Icon as IconifyIcon } from '@/components/Icon'
import {
  Alert,
  Badge,
  Button,
  Card,
  CardBody,
  Dropdown,
  DropdownItem,
  DropdownMenu,
  DropdownToggle,
  Modal,
  Placeholder,
  Spinner,
} from 'react-bootstrap'
import {useTranslation} from 'react-i18next'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'
import { useAuth } from '@/features/auth/useAuth'
import { useAccountsQuery } from '@/features/accounts/api'
import { ApiError } from '@/lib/apiClient'

import { Form } from 'react-bootstrap'

import {
  useConnectionsQuery,
  useRegisterConnectionMutation,
  useRemoveConnectionMutation,
  useRenameConnectionMutation,
  useSyncConnectionMutation,
  type ConnectionResponse,
  type ConnectionSuggestedAction,
} from './api'
import { usePluggyConnect } from './usePluggyConnect'

/** Nickname when set, otherwise the institution/connector name. */
function connectionDisplayName(c: ConnectionResponse, fallback: string): string {
  return c.customName?.trim() || c.institutionName || c.connectorName || fallback
}

/**
 * Connection health badge copy/color per suggestedAction — the same 6-state
 * copy set as v1, re-mapped from v1's Tailwind income/expense classes onto
 * Bootstrap contextual variants.
 */
function getStatusConfig(
  t: TFunction<'connections'>
): Record<ConnectionSuggestedAction, { copy: string; bg: string; spinner?: boolean }> {
  return {
    None: { copy: t('status.upToDate'), bg: 'success' },
    WaitForCompletion: { copy: t('status.syncing'), bg: 'secondary', spinner: true },
    ProvideMfa: { copy: t('status.actionNeeded'), bg: 'warning' },
    ReconnectRequired: { copy: t('status.reconnectNeeded'), bg: 'danger' },
    GenericRetry: { copy: t('status.syncIncomplete'), bg: 'secondary' },
    ReviewPartialFailure: { copy: t('status.partialSyncIssue'), bg: 'danger' },
  }
}

function formatLastSync(lastSyncAt: string | null, t: TFunction<'connections'>): string {
  if (!lastSyncAt) return t('lastSync.never')
  return t('lastSync.label', {
    date: new Intl.DateTimeFormat('pt-BR', {
      day: '2-digit',
      month: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    }).format(new Date(lastSyncAt)),
  })
}

function ConnectionStatusBadge({ suggestedAction }: { suggestedAction: ConnectionSuggestedAction }) {
  const { t } = useTranslation('connections')
  const config = getStatusConfig(t)[suggestedAction]
  return (
    <Badge bg={config.bg} className="d-inline-flex align-items-center gap-1">
      {config.spinner && <Spinner animation="border" size="sm" />}
      {config.copy}
    </Badge>
  )
}

function RowSkeleton() {
  return (
    <Card>
      <CardBody className="d-flex align-items-center justify-content-between gap-3">
        <div>
          <Placeholder as="p" animation="glow" className="mb-1">
            <Placeholder xs={7} />
          </Placeholder>
          <Placeholder as="p" animation="glow" className="mb-0">
            <Placeholder xs={5} />
          </Placeholder>
        </div>
        <Placeholder as="div" animation="glow">
          <Placeholder xs={6} style={{ width: 96, height: 30 }} />
        </Placeholder>
      </CardBody>
    </Card>
  )
}

function EmptyState({ onConnect }: { onConnect: () => void }) {
  const { t } = useTranslation('connections')
  return (
    <Card>
      <CardBody className="text-center py-5">
        <IconifyIcon icon="lucide:link" className="fs-1 text-muted mb-3" />
        <h5>{t('empty.title')}</h5>
        <p className="text-muted mb-3">{t('empty.description')}</p>
        <Button variant="primary" onClick={onConnect}>
          {t('connect')}
        </Button>
      </CardBody>
    </Card>
  )
}

interface ConnectionRowProps {
  connection: ConnectionResponse
  accountsSummary?: string
  accessToken: string
  onReconnect: (connection: ConnectionResponse) => void
  onViewDetails: (connection: ConnectionResponse) => void
  onRemove: (connection: ConnectionResponse) => void
  onRename: (connection: ConnectionResponse) => void
}

function ConnectionRow({
  connection,
  accountsSummary,
  accessToken,
  onReconnect,
  onViewDetails,
  onRemove,
  onRename,
}: ConnectionRowProps) {
  const syncMutation = useSyncConnectionMutation()
  const { showNotification } = useNotificationContext()
  const { t } = useTranslation(['connections', 'common'])

  async function handleSync() {
    try {
      await syncMutation.mutateAsync({ accessToken, id: connection.id })
      showNotification({ message: t('toast.syncStarted'), variant: 'success' })
    } catch (error) {
      if (error instanceof ApiError && error.status === 429) {
        showNotification({ message: t('toast.syncCooldown'), variant: 'danger' })
        return
      }
      showNotification({ message: t('toast.syncError'), variant: 'danger' })
    }
  }

  function renderAction() {
    switch (connection.suggestedAction) {
      case 'None':
        return (
          <Button variant="secondary" size="sm" onClick={() => void handleSync()} disabled={syncMutation.isPending}>
            {t('actions.syncNow')}
          </Button>
        )
      case 'WaitForCompletion':
        return null
      case 'ReviewPartialFailure':
        return (
          <Button variant="secondary" size="sm" onClick={() => onViewDetails(connection)}>
            {t('actions.viewDetails')}
          </Button>
        )
      case 'ProvideMfa':
        return (
          <Button variant="primary" size="sm" onClick={() => onReconnect(connection)}>
            {t('actions.completeVerification')}
          </Button>
        )
      case 'ReconnectRequired':
        return (
          <Button variant="primary" size="sm" onClick={() => onReconnect(connection)}>
            {t('actions.reconnect')}
          </Button>
        )
      case 'GenericRetry':
        return (
          <Button variant="secondary" size="sm" onClick={() => void handleSync()} disabled={syncMutation.isPending}>
            {t('actions.retrySync')}
          </Button>
        )
      default:
        return null
    }
  }

  return (
    <Card>
      <CardBody className="d-flex align-items-center justify-content-between gap-3">
        <div className="min-w-0">
          <p className="fw-medium mb-1 text-truncate">{connectionDisplayName(connection, t('fallbackName'))}</p>
          {accountsSummary && (
            <p className="text-muted fs-xs mb-1 text-truncate">{accountsSummary}</p>
          )}
          <p className="text-muted fs-xs mb-0 text-truncate">
            {connection.customName?.trim() && connection.institutionName
              ? `${connection.institutionName} · ${formatLastSync(connection.lastSyncAt, t)}`
              : formatLastSync(connection.lastSyncAt, t)}
          </p>
        </div>
        <div className="d-flex align-items-center gap-3 flex-shrink-0">
          <ConnectionStatusBadge suggestedAction={connection.suggestedAction} />
          {renderAction()}
          <Dropdown align="end">
            <DropdownToggle variant="link" className="text-muted drop-arrow-none p-0" aria-label={t('actions.connectionActions')}>
              <IconifyIcon icon="lucide:more-vertical" />
            </DropdownToggle>
            <DropdownMenu>
              <DropdownItem onClick={() => onRename(connection)}>{t('common:actions.rename')}</DropdownItem>
              <DropdownItem className="text-danger" onClick={() => onRemove(connection)}>
                {t('actions.removeConnection')}
              </DropdownItem>
            </DropdownMenu>
          </Dropdown>
        </div>
      </CardBody>
    </Card>
  )
}

/**
 * FE-CONN-01: lists every connection with its health badge +
 * suggestedAction-driven action button, opens the real Pluggy Connect
 * Widget for both the initial "Connect an institution" flow and the
 * ProvideMfa/ReconnectRequired reconnect (update-mode) flow, and removes a
 * connection only after a confirm Modal. Routed at /settings/connections —
 * this is the entry point for every other Phase 11 screen's data.
 */
export function ConnectionsPage() {
  const { t } = useTranslation(['connections', 'common'])
  const { accessToken } = useAuth()
  const token = accessToken ?? ''
  const connectionsQuery = useConnectionsQuery(token)
  const accountsQuery = useAccountsQuery(token)
  const registerConnectionMutation = useRegisterConnectionMutation()
  const removeConnectionMutation = useRemoveConnectionMutation()
  const renameConnectionMutation = useRenameConnectionMutation()
  const { openConnectWidget, isOpening } = usePluggyConnect()
  const { showNotification } = useNotificationContext()
  const [detailsConnection, setDetailsConnection] = useState<ConnectionResponse | null>(null)
  const [removeConnection, setRemoveConnection] = useState<ConnectionResponse | null>(null)
  const [renameConnection, setRenameConnection] = useState<ConnectionResponse | null>(null)
  const [renameValue, setRenameValue] = useState('')

  function openRename(connection: ConnectionResponse) {
    setRenameValue(connection.customName ?? '')
    setRenameConnection(connection)
  }

  async function handleRename() {
    if (!renameConnection) return
    try {
      await renameConnectionMutation.mutateAsync({
        accessToken: token,
        id: renameConnection.id,
        customName: renameValue,
      })
      showNotification({ message: t('rename.success'), variant: 'success' })
    } catch {
      showNotification({ message: t('rename.error'), variant: 'danger' })
    } finally {
      setRenameConnection(null)
    }
  }

  async function handleConnect() {
    await openConnectWidget({
      accessToken: token,
      onSuccess: (pluggyItemId) => {
        registerConnectionMutation.mutate(
          { accessToken: token, pluggyItemId },
          {
            onError: (error) => {
              if (error instanceof ApiError && error.status === 409) {
                showNotification({ message: t('toast.duplicate'), variant: 'danger' })
                return
              }
              showNotification({ message: t('toast.registerError'), variant: 'danger' })
            },
          }
        )
      },
    })
  }

  async function handleReconnect(connection: ConnectionResponse) {
    await openConnectWidget({
      accessToken: token,
      reconnectConnectionId: connection.id,
      onSuccess: () => {
        showNotification({ message: t('toast.verificationSubmitted'), variant: 'success' })
      },
    })
  }

  async function handleRemove() {
    if (!removeConnection) return

    const name = connectionDisplayName(removeConnection, t('fallbackName'))

    try {
      await removeConnectionMutation.mutateAsync({ accessToken: token, id: removeConnection.id })
      showNotification({ message: t('remove.success', { name }), variant: 'success' })
    } catch {
      showNotification({ message: t('remove.error'), variant: 'danger' })
    } finally {
      setRemoveConnection(null)
    }
  }

  const connections = connectionsQuery.data ?? []
  const accountsByConnectionId = new Map<string, string>()
  for (const acc of accountsQuery.data ?? []) {
    const label = acc.numberMasked ? `${acc.name} (${acc.numberMasked})` : acc.name
    const prev = accountsByConnectionId.get(acc.connectionId)
    accountsByConnectionId.set(acc.connectionId, prev ? `${prev} · ${label}` : label)
  }

  return (
    <>
      <div className="mb-3 d-flex flex-wrap align-items-center justify-content-between gap-3">
        <h4 className="mb-0">{t('title')}</h4>
        <Button
          variant="primary"
          onClick={() => void handleConnect()}
          disabled={isOpening || registerConnectionMutation.isPending}
        >
          {t('connect')}
        </Button>
      </div>

      {connectionsQuery.isError && (
        <Alert variant="danger" className="d-flex align-items-center justify-content-between gap-3">
          <span>{t('loadError')}</span>
          <Button variant="outline-danger" size="sm" onClick={() => void connectionsQuery.refetch()}>
            {t('common:actions.retry')}
          </Button>
        </Alert>
      )}

      {connectionsQuery.isPending && (
        <div className="d-flex flex-column gap-3">
          <RowSkeleton />
          <RowSkeleton />
        </div>
      )}

      {!connectionsQuery.isPending && !connectionsQuery.isError && connections.length === 0 && (
        <EmptyState onConnect={() => void handleConnect()} />
      )}

      {!connectionsQuery.isPending && !connectionsQuery.isError && connections.length > 0 && (
        <div className="d-flex flex-column gap-3">
          {registerConnectionMutation.isPending && <RowSkeleton />}
          {connections.map((connection) => (
            <ConnectionRow
              key={connection.id}
              connection={connection}
              accountsSummary={accountsByConnectionId.get(connection.id)}
              accessToken={token}
              onReconnect={(c) => void handleReconnect(c)}
              onViewDetails={setDetailsConnection}
              onRemove={setRemoveConnection}
              onRename={openRename}
            />
          ))}
        </div>
      )}

      <Modal show={!!detailsConnection} onHide={() => setDetailsConnection(null)} centered>
        <Modal.Header closeButton>
          <Modal.Title>
            {detailsConnection ? connectionDisplayName(detailsConnection, t('fallbackName')) : t('details.title')}
          </Modal.Title>
        </Modal.Header>
        <Modal.Body>{detailsConnection?.statusDetail ?? detailsConnection?.errorMessage ?? t('details.empty')}</Modal.Body>
      </Modal>

      <Modal show={!!renameConnection} onHide={() => setRenameConnection(null)} centered>
        <Modal.Header closeButton>
          <Modal.Title>{t('rename.title')}</Modal.Title>
        </Modal.Header>
        <Modal.Body>
          <Form.Group>
            <Form.Label>{t('rename.nickname')}</Form.Label>
            <Form.Control
              type="text"
              maxLength={100}
              value={renameValue}
              placeholder={renameConnection?.institutionName ?? t('rename.placeholder')}
              autoFocus
              onChange={(e) => setRenameValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void handleRename()
              }}
            />
            <Form.Text className="text-muted">{t('rename.helper')}</Form.Text>
          </Form.Group>
        </Modal.Body>
        <Modal.Footer>
          <Button variant="light" onClick={() => setRenameConnection(null)}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="primary" onClick={() => void handleRename()} disabled={renameConnectionMutation.isPending}>
            {t('common:actions.save')}
          </Button>
        </Modal.Footer>
      </Modal>

      <Modal show={!!removeConnection} onHide={() => setRemoveConnection(null)} centered>
        <Modal.Header closeButton>
          <Modal.Title>{t('remove.title')}</Modal.Title>
        </Modal.Header>
        <Modal.Body>
          {t('remove.body', {
            name: removeConnection
              ? connectionDisplayName(removeConnection, t('fallbackName'))
              : t('remove.fallbackInstitution'),
          })}
        </Modal.Body>
        <Modal.Footer>
          <Button variant="light" onClick={() => setRemoveConnection(null)}>
            {t('common:actions.cancel')}
          </Button>
          <Button variant="danger" onClick={() => void handleRemove()} disabled={removeConnectionMutation.isPending}>
            {t('actions.removeConnection')}
          </Button>
        </Modal.Footer>
      </Modal>
    </>
  )
}
