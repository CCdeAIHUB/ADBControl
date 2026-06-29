export class AdbControlError extends Error {
  constructor({ code, message, subsystem, retryable = false, details = {} }) {
    super(message);
    this.name = 'AdbControlError';
    this.code = code;
    this.subsystem = subsystem;
    this.retryable = retryable;
    this.details = details;
  }

  toResponse(requestId) {
    return {
      requestId,
      ok: false,
      error: {
        code: this.code,
        message: this.message,
        subsystem: this.subsystem,
        retryable: this.retryable,
        details: this.details,
      },
    };
  }
}

export function capabilityError(message, details = {}) {
  return new AdbControlError({
    code: 'CAPABILITY_UNAVAILABLE',
    message,
    subsystem: 'capability',
    retryable: false,
    details,
  });
}

export function permissionError(message, details = {}) {
  return new AdbControlError({
    code: 'PERMISSION_DENIED',
    message,
    subsystem: 'permission',
    retryable: false,
    details,
  });
}

export function pairingError(message, details = {}) {
  return new AdbControlError({
    code: 'PAIRING_REQUIRED',
    message,
    subsystem: 'pairing',
    retryable: true,
    details,
  });
}

export function schemaError(message, details = {}) {
  return new AdbControlError({
    code: 'SCHEMA_INVALID',
    message,
    subsystem: 'schema',
    retryable: false,
    details,
  });
}
